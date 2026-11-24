using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// Production run 36df4287: the composition played <c>staticFile('ad-01.mp4')</c>, a file that was never
/// staged, and every still died in the renderer ("Could not extract frame from compositor Error: Request
/// closed"). RenderPreviewStills, CheckLayout and RenderVideoAndUploadToStorage now refuse such a project
/// by name before Remotion runs, and a video frame-read failure says what it means and lists the staged videos.
/// </summary>
public class ReactRemotionSandboxToolsStaticFileTests
{
    private const string Table = """

        The following compositions are available:

        Main                   30      1920x1080      90 (3.00 sec)
        """;

    private const string FrameReadFailure =
        "Rendered 0/1\nError: Could not extract frame from compositor Error: Request closed\n" +
        "    at /workspace/node_modules/@remotion/renderer/dist/offthread-video-server.js:113:23";

    public static TheoryData<string> GuardedCalls() =>
    [
        "RenderPreviewStills",
        "CheckLayout",
        "RenderVideoAndUploadToStorage",
    ];

    [Theory]
    [MemberData(nameof(GuardedCalls))]
    public async Task A_composition_using_an_unstaged_file_is_refused_by_name_before_remotion_runs(string call)
    {
        Session session = new();
        session.Files["src/Root.tsx"] = "export const Root = () => <OffthreadVideo src={staticFile('ad-01.mp4')} />;";
        session.Files["public/real.mp4"] = "video";
        ReactRemotionSandboxTools tool = Build(session);

        using JsonDocument result = JsonDocument.Parse(await Invoke(tool, call));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        string error = result.RootElement.GetProperty("error").GetString()!;
        error.Should().StartWith("MISSING_STATIC_FILE:");
        error.Should().Contain("public/ad-01.mp4").And.Contain("StageProjectFile");
        result.RootElement.GetProperty("missing")[0].GetProperty("path").GetString().Should().Be("public/ad-01.mp4");
        result.RootElement.GetProperty("missing")[0].GetProperty("usedIn").GetString().Should().Be("src/Root.tsx:1");
        session.Execs.Should().BeEmpty("nothing is sent to Remotion for a project that cannot render");
    }

    [Theory]
    [InlineData("RenderPreviewStills")]
    [InlineData("RenderVideoAndUploadToStorage")]
    public async Task A_composition_whose_files_are_all_staged_runs(string call)
    {
        Session session = new();
        session.Files["src/Root.tsx"] = "export const Root = () => <OffthreadVideo src={staticFile('clips/real.mp4')} />;";
        session.Files["public/clips/real.mp4"] = "video";
        ReactRemotionSandboxTools tool = Build(session);

        string json = await Invoke(tool, call);

        json.Should().NotContain("MISSING_STATIC_FILE");
        session.Execs.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_render_of_a_clip_played_through_html5_video_is_refused_with_the_OffthreadVideo_fix()
    {
        Session session = new();
        session.Files["src/Reel.tsx"] =
            "import {Sequence, Video, staticFile} from 'remotion';\n" +
            "export const Reel = () => (\n" +
            "  <Sequence from={405}><Video src={staticFile('caffe-lume-ad-clean.mp4')} /></Sequence>\n" +
            ");";
        session.Files["public/caffe-lume-ad-clean.mp4"] = "video";
        ReactRemotionSandboxTools tool = Build(session);

        using JsonDocument result = JsonDocument.Parse(await tool.RenderVideoAndUploadToStorage("Main", "reel.mp4"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("error").GetString().Should()
            .StartWith("HTML5_VIDEO_IN_RENDER:").And.Contain("<OffthreadVideo>");
        result.RootElement.GetProperty("uses")[0].GetProperty("usedIn").GetString().Should().Be("src/Reel.tsx:3");
        session.Execs.Should().BeEmpty("a render that would capture stale frames is not started");
    }

    [Fact]
    public async Task A_still_of_a_clip_played_through_html5_video_is_not_refused()
    {
        Session session = new();
        session.Files["src/Reel.tsx"] = "import {Video, staticFile} from 'remotion';\nexport const R = () => <Video src={staticFile('a.mp4')} />;";
        session.Files["public/a.mp4"] = "video";
        ReactRemotionSandboxTools tool = Build(session);

        string json = await tool.RenderPreviewStills("Main");

        json.Should().NotContain("HTML5_VIDEO_IN_RENDER");
        session.Execs.Should().NotBeEmpty("only the full render is refused; a still shows one seeked frame");
    }

    [Fact]
    public async Task A_scan_that_cannot_read_the_sources_lets_the_command_run()
    {
        Session session = new() { FailReads = true };
        session.Files["src/Root.tsx"] = "const v = staticFile('ad-01.mp4');";
        ReactRemotionSandboxTools tool = Build(session);

        string json = await tool.RenderPreviewStills("Main");

        json.Should().NotContain("MISSING_STATIC_FILE");
        session.Execs.Should().NotBeEmpty("a check that cannot look must not block the preview");
    }

    [Fact]
    public async Task A_still_that_cannot_read_a_video_frame_says_so_and_lists_the_staged_videos()
    {
        Session session = new() { StillFailure = FrameReadFailure };
        session.Files["src/Root.tsx"] = "export const Root = () => <OffthreadVideo src={staticFile('footage/walk.mov')} />;";
        session.Files["public/footage/walk.mov"] = new string('v', 4096);
        session.Files["public/logo.png"] = "png";
        ReactRemotionSandboxTools tool = Build(session);

        using JsonDocument result = JsonDocument.Parse(await tool.RenderPreviewStills("Main"));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        JsonElement failure = result.RootElement.GetProperty("failure");
        string hint = failure.GetProperty("videoHint").GetString()!;
        hint.Should().Contain("could not read a frame from a video file").And.Contain("StageProjectFile");
        hint.Should().NotContainEquivalentOf("draw", "footage is never to be replaced by drawings");
        JsonElement videos = failure.GetProperty("stagedVideos");
        videos.GetArrayLength().Should().Be(1);
        videos[0].GetProperty("path").GetString().Should().Be("public/footage/walk.mov");
        videos[0].GetProperty("sizeBytes").GetInt64().Should().Be(4096);
    }

    [Fact]
    public async Task A_frame_read_timeout_names_the_video_and_its_size_in_pixels()
    {
        Session session = new()
        {
            StillFailure = "A delayRender() \"Fetching http://localhost:3000/proxy?src=http%3A%2F%2Flocalhost%3A3000%2Fpublic%2Ffootage%2Fwalk.mov&time=0.8 from server\" was called but not cleared after 28000ms."
        };
        session.Files["src/Root.tsx"] = "export const Root = () => <OffthreadVideo src={staticFile('footage/walk.mov')} />;";
        session.Files["public/footage/walk.mov"] = new string('v', 4096);
        session.Files["public/b.mp4"] = "v";
        session.Files[ReactRemotionSandboxTools.StagedVideosManifestPath] =
            "{\"videos\":{\"public/footage/walk.mov\":{\"path\":\"public/footage/walk.mov\",\"width\":2160,\"height\":3840,\"fpsNum\":60000,\"fpsDen\":1001,\"proxied\":false,\"sourceWidth\":2160,\"sourceHeight\":3840}}}";
        ReactRemotionSandboxTools tool = Build(session);

        using JsonDocument result = JsonDocument.Parse(await tool.RenderPreviewStills("Main"));

        JsonElement failure = result.RootElement.GetProperty("failure");
        failure.GetProperty("videoHint").GetString().Should()
            .StartWith("Remotion could not read a frame of public/footage/walk.mov (2160x3840).");
        JsonElement failing = failure.GetProperty("failingVideos");
        failing.GetArrayLength().Should().Be(1);
        failing[0].GetProperty("path").GetString().Should().Be("public/footage/walk.mov");
        failing[0].GetProperty("width").GetInt32().Should().Be(2160);
        failing[0].GetProperty("height").GetInt32().Should().Be(3840);
        failing[0].GetProperty("fps").GetDouble().Should().Be(59.94);
        JsonElement staged = failure.GetProperty("stagedVideos");
        staged.EnumerateArray().Single(v => v.GetProperty("path").GetString() == "public/b.mp4")
            .TryGetProperty("width", out _).Should().BeFalse("nothing is recorded for that one");
    }

    [Fact]
    public async Task A_still_failing_for_another_reason_gets_no_video_hint()
    {
        Session session = new() { StillFailure = "TypeError: Cannot read properties of undefined (reading 'map')" };
        session.Files["src/Root.tsx"] = "export const Root = () => null;";
        ReactRemotionSandboxTools tool = Build(session);

        string json = await tool.RenderPreviewStills("Main");

        json.Should().NotContain("videoHint").And.NotContain("stagedVideos");
    }

    [Theory]
    [InlineData("Could not extract frame from compositor", null, true)]
    [InlineData(null, "at offthread-video-server.js:113", true)]
    [InlineData("exit status 1", "Error: boom", false)]
    public void A_video_frame_read_failure_is_recognised(string? error, string? output, bool expected)
    {
        ReactRemotionSandboxTools.IsVideoFrameReadFailure(error, output).Should().Be(expected);
    }

    private static Task<string> Invoke(ReactRemotionSandboxTools tool, string call) => call switch
    {
        "RenderPreviewStills" => tool.RenderPreviewStills("Main"),
        "CheckLayout" => tool.CheckLayout("Main"),
        "RenderVideoAndUploadToStorage" => tool.RenderVideoAndUploadToStorage("Main", "promo.mp4"),
        _ => throw new ArgumentOutOfRangeException(nameof(call), call, null),
    };

    private static ReactRemotionSandboxTools Build(Session session)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
            .Build();

        WorkflowExecutionContextAccessor accessor = new();
        accessor.BeginScope(session.ExecutionId, Guid.NewGuid(), "correlation");

        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse());

        return new ReactRemotionSandboxTools(
            Mock.Of<IHttpClientFactory>(),
            accessor,
            Mock.Of<IProjectFileWorkspace>(),
            s3.Object,
            configuration,
            NullLogger<ReactRemotionSandboxTools>.Instance,
            Mock.Of<IServiceScopeFactory>(),
            sandboxSessions: new FixedSessionFactory(session));
    }

    private sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>Files in a dictionary (directories implied); compositions list the table, a still can be scripted to fail.</summary>
    private sealed class Session : ISandboxSession
    {
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public List<string[]> Execs { get; } = [];
        public string? StillFailure { get; init; }
        public bool FailReads { get; init; }

        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
        {
            Execs.Add([command, .. args]);
            if (StillFailure is not null && args.Contains("still"))
            {
                return Task.FromResult(new SandboxExecResult(
                    Succeeded: false,
                    Payload: new SandboxExecResponse(StillFailure, "execution failed: exit status 1"),
                    StatusCode: 500,
                    RawBody: JsonSerializer.Serialize(new { output = StillFailure, error = "execution failed: exit status 1" }),
                    RawError: "execution failed: exit status 1",
                    TransportError: null));
            }

            string output = args.Contains("compositions") ? Table : "Rendered 90/90";
            return Task.FromResult(new SandboxExecResult(
                Succeeded: true,
                Payload: new SandboxExecResponse(output, null),
                StatusCode: 200,
                RawBody: JsonSerializer.Serialize(new { output }),
                RawError: string.Empty,
                TransportError: null));
        }

        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
        {
            string prefix = path.TrimEnd('/') + "/";
            var entries = new Dictionary<string, SandboxFileEntry>(StringComparer.Ordinal);
            foreach ((string file, string content) in Files)
            {
                if (!file.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string rest = file[prefix.Length..];
                int slash = rest.IndexOf('/');
                string name = slash < 0 ? rest : rest[..slash];
                entries[name] = new SandboxFileEntry(name, slash >= 0, slash < 0 ? content.Length : 0, DateTime.UtcNow);
            }

            if (entries.Count == 0 && path != "out/preview")
                throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "Sandbox API request failed (404): not found");
            return Task.FromResult<IReadOnlyList<SandboxFileEntry>>([.. entries.Values]);
        }

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken)
        {
            if (FailReads)
                throw new SandboxRequestFailedException(HttpStatusCode.InternalServerError, "Sandbox API request failed (500)");
            return Files.TryGetValue(path, out string? text)
                ? Task.FromResult(new SandboxTextFile(path, text))
                : throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "Sandbox API request failed (404): file not found");
        }

        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken)
        {
            byte[] bytes = "a local render"u8.ToArray();
            HttpClient client = new();
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            return Task.FromResult(new SandboxRawFile(client, response, new MemoryStream(bytes), bytes.LongLength));
        }

        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxPublishRoute(false, null, null));

        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxInfo("s", ExecutionId.ToString(), "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => EnsureAsync(cancellationToken);

        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxStatusResponse(true, true, true, true, "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxActionResult(true));

        public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxActionResult(true));

        public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxExecResponse("installed", null));

        public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxActionResult(true));

        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxTransfer(0, null));

        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxTransfer(0, null));
    }
}
