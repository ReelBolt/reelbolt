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
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// The unchanged-render guard (saas-quality-fixes T-004). In both Luna promo runs the loop-back Author
/// rendered a film md5-identical to the one the reviewer had just failed. <c>RenderVideoAndUploadToStorage</c>
/// now digests the composition sources and staged assets it renders (src/** and public/**) and, when a later review-loop iteration renders
/// the same sources as the previous iteration's render, still uploads but says so in the result.
/// </summary>
public class ReactRemotionSandboxToolsUnchangedRenderTests
{
    private const string OutputFileName = "promo.mp4";

    private static Dictionary<string, string> Sources() => new()
    {
        ["src/root.tsx"] = "import {Composition} from 'remotion';\nexport const Root = () => null;\n",
        ["src/scenes/Hook.tsx"] = "export const Hook = () => <div>Invoices, done.</div>;\n",
        ["src/scenes/EndCard.tsx"] = "export const EndCard = () => <div>Logo</div>;\n"
    };

    [Fact]
    public async Task A_loop_back_render_of_the_same_sources_is_uploaded_and_flagged_unchanged()
    {
        Harness h = new(Sources());

        JsonElement first = await h.RenderAsync(iteration: 0);
        first.GetProperty("success").GetBoolean().Should().BeTrue();
        first.TryGetProperty("unchanged", out JsonElement firstFlag).Should().BeTrue();
        firstFlag.ValueKind.Should().Be(JsonValueKind.Null, "the first render has nothing to compare with");

        JsonElement second = await h.RenderAsync(iteration: 1);

        second.GetProperty("success").GetBoolean().Should().BeTrue("the guard never blocks the upload");
        second.GetProperty("storageKey").GetString().Should().NotBeNullOrEmpty();
        second.GetProperty("unchanged").GetBoolean().Should().BeTrue();
        second.GetProperty("unchangedWarning").GetString().Should()
            .Contain("identical to the one the reviewer judged")
            .And.Contain("reviewer feedback has not been addressed");
        h.Uploads.Should().Be(2);
    }

    [Fact]
    public async Task A_loop_back_render_after_a_source_change_is_not_flagged()
    {
        Harness h = new(Sources());
        await h.RenderAsync(iteration: 0);

        h.Files["src/scenes/Hook.tsx"] = "export const Hook = () => <div>Invoices, done in a minute.</div>;\n";
        JsonElement second = await h.RenderAsync(iteration: 1);

        second.GetProperty("unchanged").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_new_file_is_a_change()
    {
        Harness h = new(Sources());
        await h.RenderAsync(iteration: 0);

        h.Files["src/scenes/Chart.tsx"] = "export const Chart = () => null;\n";
        JsonElement second = await h.RenderAsync(iteration: 1);

        second.GetProperty("unchanged").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Rendering_twice_within_one_iteration_is_not_flagged()
    {
        Harness h = new(Sources());
        await h.RenderAsync(iteration: 0);

        JsonElement again = await h.RenderAsync(iteration: 0, force: true);

        again.GetProperty("unchanged").ValueKind.Should().Be(JsonValueKind.Null,
            "nothing has been reviewed between two renders of the same iteration");
    }

    [Fact]
    public async Task The_comparison_is_against_the_last_render_of_the_previous_iteration_not_an_earlier_render_of_this_one()
    {
        Harness h = new(Sources());
        await h.RenderAsync(iteration: 0);

        // Iteration 1 first re-renders unchanged (flagged), then edits and renders again (not flagged),
        // then reverts and renders a third time: that is the reviewed film again, so it is flagged.
        (await h.RenderAsync(iteration: 1)).GetProperty("unchanged").GetBoolean().Should().BeTrue();
        string original = h.Files["src/root.tsx"];
        h.Files["src/root.tsx"] = original + "// tweak\n";
        (await h.RenderAsync(iteration: 1, force: true)).GetProperty("unchanged").ValueKind.Should().Be(JsonValueKind.Null);
        h.Files["src/root.tsx"] = original;
        (await h.RenderAsync(iteration: 1, force: true)).GetProperty("unchanged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Sources_over_the_digest_bound_are_never_flagged()
    {
        Dictionary<string, string> many = Sources();
        for (int i = 0; i < ReactRemotionSandboxTools.MaxDigestFiles + 1; i++)
            many[$"src/gen/File{i}.ts"] = $"export const v{i} = {i};\n";
        Harness h = new(many);

        await h.RenderAsync(iteration: 0);
        JsonElement second = await h.RenderAsync(iteration: 1);

        second.GetProperty("unchanged").ValueKind.Should().Be(JsonValueKind.Null,
            "a tree too large to digest has no digest, and no digest is never 'unchanged'");
    }

    // ------------------------------------------------------------------
    // Per-iteration render keys (saas-quality-followups T-008)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Two_review_loop_iterations_upload_to_two_distinct_keys_and_the_first_keeps_its_own_object()
    {
        Harness h = new(Sources());
        Guid iterationOne = Guid.NewGuid();
        Guid iterationTwo = Guid.NewGuid();

        JsonElement first = await h.RenderAsync(iteration: 0, stepResultId: iterationOne);
        JsonElement second = await h.RenderAsync(iteration: 1, stepResultId: iterationTwo);

        string firstKey = first.GetProperty("storageKey").GetString()!;
        string secondKey = second.GetProperty("storageKey").GetString()!;
        firstKey.Should().NotBe(secondKey, "iteration 2's render must not overwrite the object iteration 1's result points at");
        h.UploadedKeys.Should().Equal(firstKey, secondKey);
        firstKey.Should().Be($"projects/{h.ProjectId}/outputFiles/{h.ExecutionId}/{iterationOne:N}/{OutputFileName}");
        secondKey.Should().Be($"projects/{h.ProjectId}/outputFiles/{h.ExecutionId}/{iterationTwo:N}/{OutputFileName}");
    }

    [Fact]
    public async Task A_render_with_no_step_result_id_keeps_the_legacy_key()
    {
        Harness h = new(Sources());

        JsonElement result = await h.RenderAsync(iteration: 0);

        result.GetProperty("storageKey").GetString()
            .Should().Be($"projects/{h.ProjectId}/outputFiles/{h.ExecutionId}/{OutputFileName}");
    }

    // ------------------------------------------------------------------
    // QA-SQF-35: the SEC-4 render-digest caps (200 dirs, 400 files, 8 MiB, depth 8)
    // ------------------------------------------------------------------

    private static async Task<bool> IsUnchangedOnLoopBackAsync(Dictionary<string, string> files)
    {
        Harness h = new(files);
        await h.RenderAsync(iteration: 0);
        JsonElement second = await h.RenderAsync(iteration: 1);
        second.GetProperty("success").GetBoolean().Should().BeTrue("the caps only disable the guard, never the render");
        return second.GetProperty("unchanged").ValueKind == JsonValueKind.True;
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task The_digest_file_cap_is_400_files(int over, bool digested)
    {
        Dictionary<string, string> files = [];
        for (int i = 0; i < ReactRemotionSandboxTools.MaxDigestFiles + over; i++)
            files[$"src/File{i}.ts"] = $"export const v{i} = {i};\n";

        ReactRemotionSandboxTools.MaxDigestFiles.Should().Be(400);
        (await IsUnchangedOnLoopBackAsync(files)).Should().Be(digested);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task The_digest_directory_cap_is_200_directories_counting_the_src_and_public_roots(int over, bool digested)
    {
        // Both roots (src and public) are listed, so 198 sub-directories is exactly 200 listings.
        ReactRemotionSandboxTools.DigestRoots.Should().Equal("src", "public");
        Dictionary<string, string> files = [];
        for (int i = 0; i < ReactRemotionSandboxTools.MaxDigestDirectories - ReactRemotionSandboxTools.DigestRoots.Length + over; i++)
            files[$"src/d{i}/File.ts"] = "export const v = 1;\n";

        ReactRemotionSandboxTools.MaxDigestDirectories.Should().Be(200);
        (await IsUnchangedOnLoopBackAsync(files)).Should().Be(digested);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task The_digest_byte_cap_is_8_MiB_of_source_text(int over, bool digested)
    {
        Dictionary<string, string> files = new()
        {
            ["src/big.ts"] = new string('a', (int)ReactRemotionSandboxTools.MaxDigestBytes + over)
        };

        ReactRemotionSandboxTools.MaxDigestBytes.Should().Be(8L * 1024 * 1024);
        (await IsUnchangedOnLoopBackAsync(files)).Should().Be(digested);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public async Task The_digest_depth_cap_is_8_levels_below_src(int depth, bool digested)
    {
        string path = "src/" + string.Concat(Enumerable.Range(0, depth).Select(i => $"l{i}/")) + "File.ts";
        Dictionary<string, string> files = new() { [path] = "export const v = 1;\n" };

        (await IsUnchangedOnLoopBackAsync(files)).Should().Be(digested);
    }

    [Fact]
    public void The_digest_is_order_independent_and_sensitive_to_path_and_content()
    {
        (string, byte[])[] a = [("src/a.ts", Encoding.UTF8.GetBytes("x")), ("src/b.ts", Encoding.UTF8.GetBytes("y"))];
        (string, byte[])[] reversed = [a[1], a[0]];
        (string, byte[])[] renamed = [("src/c.ts", Encoding.UTF8.GetBytes("x")), a[1]];
        (string, byte[])[] edited = [a[0], ("src/b.ts", Encoding.UTF8.GetBytes("z"))];
        // Moving bytes across the path boundary must not collide.
        (string, byte[])[] shifted = [("src/a.tsx", Array.Empty<byte>()), a[1]];

        string digest = ReactRemotionSandboxTools.ComputeSourceDigest(a);

        digest.Should().MatchRegex("^[0-9a-f]{64}$");
        ReactRemotionSandboxTools.ComputeSourceDigest(reversed).Should().Be(digest);
        ReactRemotionSandboxTools.ComputeSourceDigest(renamed).Should().NotBe(digest);
        ReactRemotionSandboxTools.ComputeSourceDigest(edited).Should().NotBe(digest);
        ReactRemotionSandboxTools.ComputeSourceDigest(shifted).Should().NotBe(digest);
    }

    // ------------------------------------------------------------------
    // T-206: render once per step attempt
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_second_full_render_of_the_same_composition_in_one_step_is_refused_and_uploads_nothing()
    {
        Harness h = new(Sources());
        (await h.RenderRawAsync("Main", OutputFileName)).GetProperty("success").GetBoolean().Should().BeTrue();

        JsonElement again = await h.RenderRawAsync("Main", "promo-v2.mp4");

        again.GetProperty("ok").GetBoolean().Should().BeFalse();
        again.GetProperty("error").GetString().Should().StartWith("already rendered").And.Contain("promo.mp4").And.Contain("force: true");
        h.Uploads.Should().Be(1);
    }

    [Fact]
    public async Task Force_needs_a_reason_and_with_one_the_render_goes_ahead()
    {
        Harness h = new(Sources());
        await h.RenderRawAsync("Main", OutputFileName);

        JsonElement noReason = await h.RenderRawAsync("Main", OutputFileName, force: true);
        noReason.GetProperty("ok").GetBoolean().Should().BeFalse();
        noReason.GetProperty("error").GetString().Should().Contain("forceReason");

        JsonElement forced = await h.RenderRawAsync("Main", OutputFileName, force: true, forceReason: "fixed the end card");
        forced.GetProperty("success").GetBoolean().Should().BeTrue();
        h.Uploads.Should().Be(2);
    }

    [Fact]
    public async Task Other_compositions_stills_and_a_new_step_attempt_are_never_refused()
    {
        Harness h = new(Sources());
        await h.RenderRawAsync("Main", OutputFileName);

        (await h.RenderRawAsync("LowerThird", "lower-third.mp4")).GetProperty("success").GetBoolean()
            .Should().BeTrue("a planner renders one composition per overlay");
        (await h.RenderRawAsync("Main", "cover.png")).GetProperty("success").GetBoolean()
            .Should().BeTrue("a still is not a full render");

        (await h.RenderAsync(iteration: 1)).GetProperty("success").GetBoolean()
            .Should().BeTrue("a loop-back is a new step attempt");
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed class Harness
    {
        private readonly Guid _executionId = Guid.NewGuid();
        private readonly Guid _projectId = Guid.NewGuid();
        private readonly WorkflowEngineDbContext _db;
        private readonly ReactRemotionSandboxTools _tool;
        private readonly WorkflowExecutionContextAccessor _accessor = new();

        // p2's render-once rule needs one execution-context scope per review-loop iteration.
        private int _scopeIteration;

        public Guid ExecutionId => _executionId;

        public Guid ProjectId => _projectId;

        public List<string> UploadedKeys { get; } = [];

        public Harness(Dictionary<string, string> files)
        {
            Files = files;
            _db = new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            _db.WorkflowExecutions.Add(new WorkflowExecution { Id = _executionId, ProjectId = _projectId });
            _db.SaveChanges();

            Mock<IAmazonS3> s3 = new();
            s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutObjectRequest, CancellationToken>((request, _) =>
                {
                    if (request.Key.EndsWith(OutputFileName, StringComparison.Ordinal))
                    {
                        Uploads++;
                        UploadedKeys.Add(request.Key);
                    }
                })
                .ReturnsAsync(new PutObjectResponse());

            WorkflowExecutionContextAccessor accessor = _accessor;
            accessor.BeginScope(_executionId, _projectId, "correlation");
            _accessor = accessor;

            _tool = new ReactRemotionSandboxTools(
                Mock.Of<IHttpClientFactory>(),
                accessor,
                Mock.Of<IProjectFileWorkspace>(),
                s3.Object,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
                    .Build(),
                NullLogger<ReactRemotionSandboxTools>.Instance,
                new FakeScopeFactory(_db),
                mediaProbe: null,
                usageRecorder: null,
                videoToolRunner: null,
                videoEditingOptions: null,
                sandboxSessions: new FixedSessionFactory(new FileTreeSession(_executionId, files)));
        }

        public Dictionary<string, string> Files { get; }

        public async Task<JsonElement> RenderRawAsync(string compositionId, string fileName, bool force = false, string? forceReason = null)
        {
            string json = await _tool.RenderVideoAndUploadToStorage(compositionId, fileName, force: force, forceReason: forceReason);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public int Uploads { get; private set; }

        /// <summary>
        /// Each review-loop iteration is a new step attempt in production (a new execution-context scope), so the
        /// harness opens one per iteration; a second render inside one iteration must be forced (T-206 render once).
        /// </summary>
        public async Task<JsonElement> RenderAsync(int iteration, bool force = false, Guid? stepResultId = null)
        {
            if (iteration != _scopeIteration || stepResultId is not null)
            {
                _accessor.BeginScope(_executionId, _projectId, "correlation", stepResultId: stepResultId);
                _scopeIteration = iteration;
            }

            WorkflowExecution execution = await _db.WorkflowExecutions.SingleAsync(e => e.Id == _executionId);
            execution.IterationCount = iteration;
            await _db.SaveChangesAsync();

            string json = await _tool.RenderVideoAndUploadToStorage("Main", OutputFileName,
                force: force, forceReason: force ? "edited the scene" : null);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    internal sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>A local workspace session over an in-memory file tree; renders always succeed.</summary>
    internal sealed class FileTreeSession(Guid executionId, Dictionary<string, string> files) : ISandboxSession
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
            => Task.FromResult(new SandboxExecResult(
                Succeeded: true,
                Payload: new SandboxExecResponse("rendered", null),
                StatusCode: 0,
                RawBody: "{\"output\":\"rendered\"}",
                RawError: string.Empty,
                TransportError: null));

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
