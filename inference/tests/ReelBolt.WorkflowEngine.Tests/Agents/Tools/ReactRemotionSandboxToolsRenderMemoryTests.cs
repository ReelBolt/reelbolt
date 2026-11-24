using System;
using System.Collections.Generic;
using System.Globalization;
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
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// A full render is kept inside the sandbox's memory limit: memory-safe Remotion defaults when the
/// model set none, and one automatic retry with <c>--concurrency=1</c> and a smaller video cache when
/// the kernel killed the render (production run 571c3adf: "Compositor quit with signal SIGKILL", a
/// memory-cgroup OOM, while several staged videos played at once).
/// </summary>
public class ReactRemotionSandboxToolsRenderMemoryTests
{
    private const string OutputFileName = "promo.mp4";
    private const string ExitStatusOne = "execution failed: exit status 1";
    private const string OomOutput = "Rendered 352/720\nError: Compositor quit with signal SIGKILL\nMemory cgroup out of memory: Killed process (remotion)";

    private static readonly RenderOutcome Ok = new(true, string.Empty, null);
    private static readonly RenderOutcome Oom = new(false, OomOutput, ExitStatusOne);
    private static readonly RenderOutcome Ordinary = new(false, "Error: Element type is invalid", ExitStatusOne);

    // ------------------------------------------------------------------
    // The arg helpers.
    // ------------------------------------------------------------------

    [Fact]
    public void Defaults_are_added_when_the_model_set_neither_flag()
    {
        List<string> args = ReactRemotionSandboxTools.WithRenderMemoryDefaults(["Main", "out/promo.mp4", "--scale=1"], 2, 268435456);

        args.Should().Equal("Main", "out/promo.mp4", "--scale=1", "--concurrency=2", "--offthreadvideo-cache-size-in-bytes=268435456");
    }

    [Fact]
    public void Model_supplied_values_are_kept_in_either_form()
    {
        ReactRemotionSandboxTools.WithRenderMemoryDefaults(["Main", "out/a.mp4", "--concurrency=6", "--offthreadvideo-cache-size-in-bytes", "1000"], 2, 268435456)
            .Should().Equal("Main", "out/a.mp4", "--concurrency=6", "--offthreadvideo-cache-size-in-bytes", "1000");

        ReactRemotionSandboxTools.WithRenderMemoryDefaults(["Main", "out/a.mp4", "--concurrency", "50%"], 2, 268435456)
            .Should().Equal("Main", "out/a.mp4", "--concurrency", "50%", "--offthreadvideo-cache-size-in-bytes=268435456");
    }

    [Fact]
    public void A_look_alike_flag_does_not_count_as_concurrency()
    {
        ReactRemotionSandboxTools.WithRenderMemoryDefaults(["--concurrencies=1,2"], 2, 1024)
            .Should().Contain("--concurrency=2");
    }

    [Fact]
    public void The_retry_args_replace_both_flags_with_one_tab_and_half_the_cache()
    {
        ReactRemotionSandboxTools.OutOfMemoryRetryArgs(
                ["Main", "out/a.mp4", "--concurrency=2", "--offthreadvideo-cache-size-in-bytes=268435456", "--chromium-executable=/x"], 268435456)
            .Should().Equal("Main", "out/a.mp4", "--chromium-executable=/x", "--concurrency=1", "--offthreadvideo-cache-size-in-bytes=134217728");

        // The model's own cache (separate-value form) is halved; the model's concurrency is replaced.
        ReactRemotionSandboxTools.OutOfMemoryRetryArgs(
                ["Main", "out/a.mp4", "--concurrency", "8", "--offthreadvideo-cache-size-in-bytes", "1073741824"], 268435456)
            .Should().Equal("Main", "out/a.mp4", "--concurrency=1", "--offthreadvideo-cache-size-in-bytes=536870912");
    }

    [Fact]
    public void The_retry_cache_has_a_floor_and_never_grows()
    {
        ReactRemotionSandboxTools.OutOfMemoryRetryArgs(["--offthreadvideo-cache-size-in-bytes=40000000"], 268435456)
            .Should().Contain("--offthreadvideo-cache-size-in-bytes=" + ReactRemotionSandboxTools.MinOutOfMemoryRetryCacheBytes.ToString(CultureInfo.InvariantCulture));

        ReactRemotionSandboxTools.OutOfMemoryRetryArgs(["--offthreadvideo-cache-size-in-bytes=1000"], 268435456)
            .Should().Contain("--offthreadvideo-cache-size-in-bytes=1000");

        // Not a number: half of the configured default.
        ReactRemotionSandboxTools.OutOfMemoryRetryArgs(["--offthreadvideo-cache-size-in-bytes=lots"], 268435456)
            .Should().Contain("--offthreadvideo-cache-size-in-bytes=134217728");
    }

    [Fact]
    public void The_retry_deadline_is_stretched_but_stays_inside_the_policy()
    {
        RenderTimeoutPolicy policy = new(0.5, 300, 600, 6 * 3600);
        policy.Stretch(900, ReactRemotionSandboxTools.OutOfMemoryRetryDeadlineFactor).Should().Be(1800);
        policy.Stretch(5 * 3600, 2.0).Should().Be(6 * 3600);
        policy.Stretch(100, 2.0).Should().Be(600);
    }

    // ------------------------------------------------------------------
    // Through the real tool.
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_render_carries_the_shipped_defaults()
    {
        RenderHarness h = new([Ok]);

        JsonElement result = await h.RenderAsync();

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        h.Renders.Should().ContainSingle();
        h.Renders[0].Args.Should().Contain("--concurrency=2")
            .And.Contain("--offthreadvideo-cache-size-in-bytes=268435456");
        ReactRemotionSandboxTools.DefaultRenderConcurrency.Should().Be(2);
        ReactRemotionSandboxTools.DefaultOffthreadVideoCacheBytes.Should().Be(256L * 1024 * 1024);
    }

    [Fact]
    public async Task The_defaults_are_configurable_and_the_models_own_values_win()
    {
        RenderHarness configured = new([Ok], settings: new()
        {
            [ReactRemotionSandboxTools.RenderConcurrencyKey] = "3",
            [ReactRemotionSandboxTools.OffthreadVideoCacheBytesKey] = "100000000"
        });
        await configured.RenderAsync();
        configured.Renders[0].Args.Should().Contain("--concurrency=3").And.Contain("--offthreadvideo-cache-size-in-bytes=100000000");

        RenderHarness model = new([Ok]);
        await model.RenderAsync(remotionArgs: ["--concurrency=4", "--offthreadvideo-cache-size-in-bytes=512"]);
        model.Renders[0].Args.Where(a => a.StartsWith("--concurrency", StringComparison.Ordinal)).Should().Equal("--concurrency=4");
        model.Renders[0].Args.Where(a => a.StartsWith("--offthreadvideo-cache-size-in-bytes", StringComparison.Ordinal))
            .Should().Equal("--offthreadvideo-cache-size-in-bytes=512");
    }

    [Fact]
    public async Task A_killed_render_is_retried_once_with_concurrency_one_and_the_retry_succeeds()
    {
        RenderHarness h = new([Oom, Ok]);
        RenderFailureLedger ledger = new();

        JsonElement result;
        using (ledger.Publish())
            result = await h.RenderAsync();

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        h.Renders.Should().HaveCount(2);
        h.Renders[1].Args.Should().Contain("--concurrency=1")
            .And.Contain("--offthreadvideo-cache-size-in-bytes=134217728")
            .And.NotContain("--concurrency=2")
            .And.Contain(a => a.StartsWith("--chromium-executable=", StringComparison.Ordinal));
        h.Renders[1].TimeoutSeconds.Should().BeGreaterThan(h.Renders[0].TimeoutSeconds);
        h.Uploads.Should().Be(1);
        ledger.HasFailed.Should().BeFalse("the killed first attempt is not a failed render of its own");
    }

    [Fact]
    public async Task The_retry_is_not_a_second_render_for_the_render_once_guard()
    {
        RenderHarness h = new([Oom, Ok, Ok]);

        (await h.RenderAsync()).GetProperty("success").GetBoolean().Should().BeTrue();

        // The call above is ONE successful render: a second call is refused as "already rendered",
        // not because the retry counted, and nothing more reaches the sandbox.
        JsonElement again = await h.RenderAsync();
        again.GetProperty("ok").GetBoolean().Should().BeFalse();
        again.GetProperty("error").GetString().Should().StartWith("already rendered");
        h.Renders.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_render_killed_twice_records_one_failure_and_tells_the_model_it_ran_out_of_memory()
    {
        RenderHarness h = new([Oom, Oom]);
        RenderFailureLedger ledger = new();

        JsonElement result;
        using (ledger.Publish())
            result = await h.RenderAsync();

        h.Renders.Should().HaveCount(2, "the retry happens exactly once");
        ledger.FailedCount("Main").Should().Be(1);
        ledger.LastMessage.Should().Contain("out of memory");

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("outOfMemory").GetBoolean().Should().BeTrue();
        result.GetProperty("message").GetString().Should().Contain("ran out of memory").And.Contain("--concurrency=1");
        string guidance = string.Join(" ", result.GetProperty("guidance").EnumerateArray().Select(g => g.GetString()));
        guidance.Should().Contain("fewer videos decoding at the same moment")
            .And.Contain("shorter source clips")
            .And.Contain("Keep the real footage");
        result.GetProperty("outputTail").GetString().Should().Contain("SIGKILL", "the renderer's own output is still there");
    }

    [Fact]
    public async Task An_ordinary_failure_is_not_retried()
    {
        RenderHarness h = new([Ordinary, Ok]);
        RenderFailureLedger ledger = new();

        JsonElement result;
        using (ledger.Publish())
            result = await h.RenderAsync();

        h.Renders.Should().ContainSingle();
        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.TryGetProperty("outOfMemory", out _).Should().BeFalse();
        ledger.FailedCount("Main").Should().Be(1);
    }

    [Fact]
    public async Task A_killed_render_whose_retry_fails_otherwise_keeps_the_ordinary_error()
    {
        RenderHarness h = new([Oom, Ordinary]);

        JsonElement result = await h.RenderAsync();

        h.Renders.Should().HaveCount(2);
        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.TryGetProperty("outOfMemory", out _).Should().BeFalse();
    }

    private static readonly Dictionary<string, string?> OneSecondDeadline = new()
    {
        ["Compute:Render:Timeout:MinSeconds"] = "1",
        ["Compute:Render:Timeout:MaxSeconds"] = "1"
    };

    [Fact]
    public async Task A_kill_at_the_render_deadline_is_a_timeout_and_is_not_retried()
    {
        RenderHarness h = new([new RenderOutcome(false, string.Empty, "execution failed: signal: killed", DelayMs: 1100)], OneSecondDeadline);
        RenderFailureLedger ledger = new();

        JsonElement result;
        using (ledger.Publish())
            result = await h.RenderAsync();

        h.Renders.Should().HaveCount(1);
        result.GetProperty("timedOut").GetBoolean().Should().BeTrue();
        result.TryGetProperty("outOfMemory", out _).Should().BeFalse();
        result.GetProperty("message").GetString().Should().Contain("time limit").And.NotContain("memory");
        ledger.LastMessageFor("Main").Should().Contain("time limit").And.NotContain("memory");
    }

    [Fact]
    public async Task The_same_kill_text_well_before_the_deadline_is_one_out_of_memory_retry()
    {
        RenderHarness h = new([new RenderOutcome(false, string.Empty, "execution failed: signal: killed"), new RenderOutcome(false, string.Empty, "execution failed: signal: killed")], OneSecondDeadline);

        JsonElement result = await h.RenderAsync();

        h.Renders.Should().HaveCount(2);
        result.GetProperty("outOfMemory").GetBoolean().Should().BeTrue();
        result.TryGetProperty("timedOut", out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_failed_render_cap_counts_calls_not_retries()
    {
        RenderHarness h = new([Oom, Oom, Oom, Oom, Ok]);
        RenderFailureLedger ledger = new();

        using (ledger.Publish())
            (await h.RenderAsync()).GetProperty("success").GetBoolean().Should().BeFalse();
        ledger.FailedCount("Main").Should().Be(1);

        using (ledger.Publish())
            (await h.RenderAsync()).GetProperty("success").GetBoolean().Should().BeFalse();
        ledger.FailedCount("Main").Should().Be(2);

        using (ledger.Publish())
        {
            JsonElement third = await h.RenderAsync();
            third.GetProperty("terminal").GetBoolean().Should().BeTrue();
        }

        h.Renders.Should().HaveCount(4, "two calls, each with its one retry; the capped third call never runs");
        ledger.FailedCount("Main").Should().Be(2);
    }

    [Fact]
    public async Task The_video_cache_is_sized_from_the_staged_videos()
    {
        // Six 1080x1920 delivery copies recorded at staging (the runs 36df4287 / 9bf15ff8 grid): the flat
        // 256 MiB starved them; the sized cache is the 1 GiB ceiling, and the OOM retry halves THAT.
        Dictionary<string, string> files = [];
        Dictionary<string, object> videos = [];
        foreach (string name in new[] { "a", "b", "c", "d", "e", "f" })
        {
            files[$"public/{name}.mp4"] = "x";
            videos[$"public/{name}.mp4"] = new { path = $"public/{name}.mp4", width = 1080, height = 1920, fpsNum = 30, fpsDen = 1, proxied = true, sourceWidth = 2160, sourceHeight = 3840 };
        }
        files[ReactRemotionSandboxTools.StagedVideosManifestPath] = JsonSerializer.Serialize(new { videos });

        RenderHarness h = new([Oom, Ok], files: files);
        (await h.RenderAsync()).GetProperty("success").GetBoolean().Should().BeTrue();

        h.Renders[0].Args.Should().Contain("--offthreadvideo-cache-size-in-bytes=" + ReactRemotionSandboxTools.DefaultOffthreadVideoCacheMaxBytes.ToString(CultureInfo.InvariantCulture));
        h.Renders[1].Args.Should().Contain("--offthreadvideo-cache-size-in-bytes=" + (ReactRemotionSandboxTools.DefaultOffthreadVideoCacheMaxBytes / 2).ToString(CultureInfo.InvariantCulture))
            .And.Contain("--concurrency=1");
    }

    [Fact]
    public async Task An_unrecorded_staged_video_is_budgeted_as_a_1080p_frame_and_the_models_cache_still_wins()
    {
        Dictionary<string, string> files = new() { ["public/clips/a.mp4"] = "x", ["public/clips/b.mov"] = "x", ["public/c.mp4"] = "x", ["public/logo.png"] = "x" };

        RenderHarness h = new([Ok], files: files);
        await h.RenderAsync();
        long expected = 2L * 3 * ReactRemotionSandboxTools.OffthreadVideoFramesPerVideo * ReactRemotionSandboxTools.UnknownVideoFrameBytes;
        h.Renders[0].Args.Should().Contain("--offthreadvideo-cache-size-in-bytes=" + expected.ToString(CultureInfo.InvariantCulture));

        RenderHarness model = new([Ok], files: files);
        await model.RenderAsync(remotionArgs: ["--offthreadvideo-cache-size-in-bytes=512"]);
        model.Renders[0].Args.Where(a => a.StartsWith("--offthreadvideo-cache-size-in-bytes", StringComparison.Ordinal))
            .Should().Equal("--offthreadvideo-cache-size-in-bytes=512");

        RenderHarness capped = new([Ok], settings: new() { [ReactRemotionSandboxTools.OffthreadVideoCacheMaxBytesKey] = "300000000" }, files: files);
        await capped.RenderAsync();
        capped.Renders[0].Args.Should().Contain("--offthreadvideo-cache-size-in-bytes=300000000");
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed record RenderOutcome(bool Succeeded, string Output, string? Error, int DelayMs = 0);

    private sealed record RenderCall(IReadOnlyList<string> Args, int TimeoutSeconds);

    private sealed class RenderHarness
    {
        private readonly ReactRemotionSandboxTools _tool;
        private readonly ScriptedSession _session;

        public int Uploads { get; private set; }
        public IReadOnlyList<RenderCall> Renders => _session.Renders;

        public RenderHarness(
            IEnumerable<RenderOutcome> renders, Dictionary<string, string?>? settings = null, Dictionary<string, string>? files = null)
        {
            Guid executionId = Guid.NewGuid();
            Guid projectId = Guid.NewGuid();
            WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = projectId });
            db.SaveChanges();

            Mock<IAmazonS3> s3 = new();
            s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutObjectRequest, CancellationToken>((request, _) =>
                {
                    if (request.Key.EndsWith(OutputFileName, StringComparison.Ordinal))
                        Uploads++;
                })
                .ReturnsAsync(new PutObjectResponse());

            WorkflowExecutionContextAccessor accessor = new();
            accessor.BeginScope(executionId, projectId, "correlation");
            _session = new ScriptedSession(executionId, new Queue<RenderOutcome>(renders), files);

            Dictionary<string, string?> config = new() { ["MinIO:BucketName"] = "reelforge" };
            foreach ((string key, string? value) in settings ?? [])
                config[key] = value;

            _tool = new ReactRemotionSandboxTools(
                Mock.Of<IHttpClientFactory>(),
                accessor,
                Mock.Of<IProjectFileWorkspace>(),
                s3.Object,
                new ConfigurationBuilder().AddInMemoryCollection(config).Build(),
                NullLogger<ReactRemotionSandboxTools>.Instance,
                new ReelBolt.WorkflowEngine.Tests.FakeScopeFactory(db),
                sandboxSessions: new FixedSessionFactory(_session));
        }

        public async Task<JsonElement> RenderAsync(string[]? remotionArgs = null)
        {
            string json = await _tool.RenderVideoAndUploadToStorage("Main", OutputFileName, remotionArgs);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>A local workspace whose full renders follow a script, in order; every other command succeeds.</summary>
    private sealed class ScriptedSession(Guid executionId, Queue<RenderOutcome> renders, Dictionary<string, string>? extraFiles) : ISandboxSession
    {
        private static readonly byte[] RenderBytes = Encoding.ASCII.GetBytes("render");
        private readonly Dictionary<string, string> Files = new Dictionary<string, string>
        {
            ["src/root.tsx"] = "import {Composition} from 'remotion';\nexport const Root = () => null;\n"
        }.Concat(extraFiles ?? []).ToDictionary(kv => kv.Key, kv => kv.Value);

        private readonly List<RenderCall> _renders = [];

        public Guid ExecutionId { get; } = executionId;
        public IReadOnlyList<RenderCall> Renders => _renders;

        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (command == "npx" && args.Count >= 2 && args[0] == "remotion" && args[1] == "render")
            {
                _renders.Add(new RenderCall(args.Skip(2).ToList(), timeoutSeconds));
                RenderOutcome outcome = renders.Count > 0 ? renders.Dequeue() : Ok;
                if (outcome.DelayMs > 0)
                    Thread.Sleep(outcome.DelayMs);
                if (!outcome.Succeeded)
                {
                    return Task.FromResult(new SandboxExecResult(
                        Succeeded: false,
                        Payload: new SandboxExecResponse(outcome.Output, outcome.Error),
                        StatusCode: 500,
                        RawBody: "{}",
                        RawError: outcome.Error ?? string.Empty,
                        TransportError: null));
                }
            }

            return Task.FromResult(new SandboxExecResult(
                Succeeded: true,
                Payload: new SandboxExecResponse(string.Empty, null),
                StatusCode: 200,
                RawBody: "{\"output\":\"\"}",
                RawError: "{\"output\":\"\"}",
                TransportError: null));
        }

        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
        {
            string prefix = path.TrimEnd('/') + "/";
            List<SandboxFileEntry> entries = Files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[prefix.Length..])
                .Select(rest => rest.Contains('/')
                    ? new SandboxFileEntry(rest[..rest.IndexOf('/')], true, 0, DateTime.UnixEpoch)
                    : new SandboxFileEntry(rest, false, Encoding.UTF8.GetByteCount(Files[prefix + rest]), DateTime.UnixEpoch))
                .DistinctBy(e => e.Name)
                .ToList();
            return Task.FromResult<IReadOnlyList<SandboxFileEntry>>(entries);
        }

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            Files.TryGetValue(path, out string? text)
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

        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxInfo("s", ExecutionId.ToString(), "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => EnsureAsync(cancellationToken);

        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxStatusResponse(true, true, true, true, "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxActionResult(true));

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
