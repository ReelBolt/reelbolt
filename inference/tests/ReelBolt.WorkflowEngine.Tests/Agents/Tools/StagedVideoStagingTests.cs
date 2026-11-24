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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// StageProjectFile stages an oversized video as an engine-made delivery copy under the same name, caches
/// it by the source object, tells the agent, records it for the render's cache sizing, and falls back to
/// the original on any failure. The render's <c>&lt;OffthreadVideo&gt;</c> cache is sized from the record.
/// </summary>
public class StagedVideoStagingTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    [Fact]
    public async Task A_4k_video_is_staged_as_a_1080_copy_under_its_own_name()
    {
        Harness h = new();
        ProjectWorkspaceFile clip = h.AddFile("ad-latte-art-topdown.mp4", 2160, 3840);

        JsonElement result = await h.StageAsync(clip);

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("path").GetString().Should().Be("public/ad-latte-art-topdown.mp4");
        result.GetProperty("staticFile").GetString().Should().Be("ad-latte-art-topdown.mp4");
        JsonElement video = result.GetProperty("video");
        video.GetProperty("proxied").GetBoolean().Should().BeTrue();
        video.GetProperty("width").GetInt32().Should().Be(1080);
        video.GetProperty("height").GetInt32().Should().Be(1920);
        video.GetProperty("sourceWidth").GetInt32().Should().Be(2160);
        video.GetProperty("sourceHeight").GetInt32().Should().Be(3840);
        video.GetProperty("fps").GetDouble().Should().Be(30);
        video.GetProperty("note").GetString().Should().Contain("same frames per second").And.Contain("same name");

        h.FfmpegRuns.Should().ContainSingle();
        h.FfmpegRuns[0].Should().ContainInOrder("-fps_mode", "passthrough");
        string copyKey = h.Store.Keys.Single(k => k.EndsWith(".mp4", StringComparison.Ordinal) && k.Contains("/agentFiles/render-proxies/", StringComparison.Ordinal));
        copyKey.Should().StartWith($"projects/{ProjectId}/agentFiles/render-proxies/");
        h.Session.ByteWrites.Should().ContainSingle();
        h.Session.ByteWrites[0].Path.Should().Be("public/ad-latte-art-topdown.mp4");
        h.Session.ByteWrites[0].Content.Should().Equal(Harness.CopyBytes, "the copy, not the 4K original, reaches the sandbox");

        string manifest = h.Session.Writes.Last(w => w.Path == ReactRemotionSandboxTools.StagedVideosManifestPath).Content;
        manifest.Should().Contain("\"public/ad-latte-art-topdown.mp4\"").And.Contain("\"proxied\":true").And.Contain("\"width\":1080");
    }

    [Fact]
    public async Task The_second_staging_of_the_same_object_reuses_the_copy()
    {
        Harness h = new();
        ProjectWorkspaceFile clip = h.AddFile("a.mp4", 2160, 3840);

        await h.StageAsync(clip);
        JsonElement again = await h.StageAsync(clip, "public/clips/a.mp4");

        again.GetProperty("video").GetProperty("proxied").GetBoolean().Should().BeTrue();
        h.FfmpegRuns.Should().ContainSingle("the copy and its facts are cached by the source object");
        h.Probes.Should().Be(2, "the source and the copy were probed once, the first time");
        h.Session.ByteWrites.Select(w => w.Path).Should().Equal("public/a.mp4", "public/clips/a.mp4");
        h.Session.ByteWrites.Should().OnlyContain(w => w.Content.SequenceEqual(Harness.CopyBytes));
    }

    [Fact]
    public async Task A_video_within_size_is_staged_as_it_is_and_still_recorded()
    {
        Harness h = new();
        ProjectWorkspaceFile clip = h.AddFile("small.mp4", 1080, 1920);

        JsonElement result = await h.StageAsync(clip);

        result.GetProperty("video").GetProperty("proxied").GetBoolean().Should().BeFalse();
        result.GetProperty("video").TryGetProperty("note", out _).Should().BeFalse();
        h.FfmpegRuns.Should().BeEmpty();
        h.Session.ByteWrites.Single().Content.Should().Equal(Harness.SourceBytes(clip.StorageKey));
        h.Session.Writes.Should().Contain(w => w.Path == ReactRemotionSandboxTools.StagedVideosManifestPath);

        await h.StageAsync(clip, "public/again.mp4");
        h.Probes.Should().Be(1, "the decision is cached too, so a second staging does not download and probe again");
    }

    [Fact]
    public async Task A_failed_copy_stages_the_original_and_the_staging_still_succeeds()
    {
        Harness h = new() { FfmpegFails = true };
        ProjectWorkspaceFile clip = h.AddFile("big.mp4", 3840, 2160);

        JsonElement result = await h.StageAsync(clip);

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("video").GetProperty("proxied").GetBoolean().Should().BeFalse();
        result.GetProperty("video").GetProperty("width").GetInt32().Should().Be(3840);
        h.Session.ByteWrites.Single().Content.Should().Equal(Harness.SourceBytes(clip.StorageKey));
    }

    [Fact]
    public async Task A_copy_whose_frame_rate_changed_is_not_used()
    {
        Harness h = new() { CopyFps = "25/1" };
        ProjectWorkspaceFile clip = h.AddFile("big.mp4", 2160, 3840);

        JsonElement result = await h.StageAsync(clip);

        result.GetProperty("video").GetProperty("proxied").GetBoolean().Should().BeFalse();
        h.Session.ByteWrites.Single().Content.Should().Equal(Harness.SourceBytes(clip.StorageKey));
    }

    [Fact]
    public async Task Without_a_video_runner_a_video_is_staged_exactly_as_before()
    {
        Harness h = new(withRunner: false);
        ProjectWorkspaceFile clip = h.AddFile("big.mp4", 2160, 3840);

        JsonElement result = await h.StageAsync(clip);

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.TryGetProperty("video", out _).Should().BeFalse();
        h.Session.ByteWrites.Single().Content.Should().Equal(Harness.SourceBytes(clip.StorageKey));
        h.Session.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_image_never_goes_through_the_video_path()
    {
        Harness h = new();
        ProjectWorkspaceFile logo = h.AddFile("logo.png", 4000, 4000, mime: "image/png");

        JsonElement result = await h.StageAsync(logo);

        result.TryGetProperty("video", out _).Should().BeFalse();
        h.Probes.Should().Be(0);
    }

    // --- Harness ---------------------------------------------------------------------------------

    private sealed class Harness
    {
        public static readonly byte[] CopyBytes = Encoding.ASCII.GetBytes("copy-1080");

        public static byte[] SourceBytes(string key) => Encoding.UTF8.GetBytes("source:" + key);

        private readonly List<ProjectWorkspaceFile> _files = [];
        private readonly Dictionary<string, (int W, int H)> _sizes = [];
        private readonly ReactRemotionSandboxTools _tool;

        public Dictionary<string, byte[]> Store { get; } = new(StringComparer.Ordinal);
        public RecordingSession Session { get; } = new();
        public List<IReadOnlyList<string>> FfmpegRuns { get; } = [];
        public int Probes { get; private set; }
        public bool FfmpegFails { get; init; }
        public string CopyFps { get; init; } = "30/1";

        public Harness(bool withRunner = true)
        {
            Mock<IAmazonS3> s3 = new();
            s3.Setup(x => x.GetObjectMetadataAsync("reelforge", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string key, CancellationToken _) => new GetObjectMetadataResponse { ETag = "\"etag-" + key.Length + "\"" });
            s3.Setup(x => x.GetObjectAsync("reelforge", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string key, CancellationToken _) =>
                {
                    if (!Store.TryGetValue(key, out byte[]? bytes))
                        throw new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound };
                    return new GetObjectResponse { ResponseStream = new MemoryStream(bytes) };
                });
            s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutObjectRequest, CancellationToken>((request, _) =>
                {
                    if (request.InputStream is { } stream)
                    {
                        using MemoryStream copy = new();
                        stream.CopyTo(copy);
                        Store[request.Key] = copy.ToArray();
                    }
                    else
                    {
                        Store[request.Key] = Encoding.UTF8.GetBytes(request.ContentBody ?? string.Empty);
                    }
                })
                .ReturnsAsync(new PutObjectResponse());

            Mock<IVideoToolRunner> runner = new();
            runner.Setup(x => x.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string> args, TimeSpan _, CancellationToken _) =>
                {
                    Probes++;
                    args.Should().ContainInOrder("-format_whitelist", MediaDerivativeInputFormats.Whitelist);
                    string path = args[^1];
                    if (path.EndsWith("copy.mp4", StringComparison.Ordinal))
                        return Ok(ProbeJson(1080, 1920, CopyFps));
                    string content = Encoding.UTF8.GetString(File.ReadAllBytes(path));
                    string key = content["source:".Length..];
                    (int w, int h) = _sizes[key];
                    return Ok(ProbeJson(w, h, "30/1"));
                });
            runner.Setup(x => x.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string> args, TimeSpan _, CancellationToken _) =>
                {
                    FfmpegRuns.Add(args);
                    if (FfmpegFails)
                        return new VideoToolResult(1, "", "Conversion failed!", false);
                    File.WriteAllBytes(args[^1], CopyBytes);
                    return Ok("");
                });

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
                .Build();
            WorkflowExecutionContextAccessor accessor = new();
            accessor.BeginScope(Session.ExecutionId, ProjectId, "correlation", null);
            Mock<IProjectFileWorkspace> workspace = new();
            workspace.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(() => _files);

            _tool = new ReactRemotionSandboxTools(
                Mock.Of<IHttpClientFactory>(),
                accessor,
                workspace.Object,
                s3.Object,
                configuration,
                NullLogger<ReactRemotionSandboxTools>.Instance,
                Mock.Of<IServiceScopeFactory>(),
                videoToolRunner: withRunner ? runner.Object : null,
                sandboxSessions: new FixedSessionFactory(Session));
        }

        public ProjectWorkspaceFile AddFile(string name, int width, int height, string mime = "video/mp4")
        {
            string key = $"projects/{ProjectId}/userFiles/{name}";
            ProjectWorkspaceFile file = new(Guid.NewGuid(), ProjectId, name, null, "userFiles", key, mime, 1000, DateTime.UtcNow, null);
            _files.Add(file);
            _sizes[key] = (width, height);
            Store[key] = SourceBytes(key);
            return file;
        }

        public async Task<JsonElement> StageAsync(ProjectWorkspaceFile file, string? sandboxPath = null)
        {
            string json = await _tool.StageProjectFile(file.Id.ToString(), sandboxPath);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static VideoToolResult Ok(string stdout) => new(0, stdout, "", false);

        private static string ProbeJson(int width, int height, string fps) =>
            "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":" + width + ",\"height\":" + height +
            ",\"r_frame_rate\":\"" + fps + "\",\"avg_frame_rate\":\"" + fps + "\",\"pix_fmt\":\"yuv420p\"}]," +
            "\"format\":{\"duration\":\"10.0\",\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\"}}";
    }

    private sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>A session that keeps text files and records byte writes; nothing else is used.</summary>
    private sealed class RecordingSession : ISandboxSession
    {
        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);

        public Guid ExecutionId { get; } = Guid.NewGuid();
        public List<(string Path, string Content)> Writes { get; } = [];
        public List<(string Path, byte[] Content)> ByteWrites { get; } = [];

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            _text.TryGetValue(path, out string? text)
                ? Task.FromResult(new SandboxTextFile(path, text))
                : throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "not found");

        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            Writes.Add((path, content));
            _text[path] = content;
            return Task.FromResult(new SandboxActionResult(true));
        }

        public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
        {
            ByteWrites.Add((path, content));
            return Task.CompletedTask;
        }

        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
