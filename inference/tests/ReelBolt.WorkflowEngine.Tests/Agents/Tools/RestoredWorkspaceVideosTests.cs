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
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.Compositions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// A restored workspace's videos get the treatment a fresh staging gives them (run 9e22258a: a revision
/// restored a workspace captured before staged videos were copied and its render stopped on a 2160x3840
/// original): an oversized unrecorded video is replaced by the engine-made copy under the same name and
/// recorded; a video mapped to a project file comes from the proxy cache without reading the sandbox; a
/// small one is left alone; any failure leaves the original.
/// </summary>
public class RestoredWorkspaceVideosTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    [Fact]
    public async Task An_oversized_unrecorded_video_is_swapped_for_the_copy_and_recorded()
    {
        Harness h = new();
        WorkspaceSession session = h.RestoredSession(("public/ad-pour-warm-light.mp4", "sandbox-pour", 2160, 3840));

        RestoredVideosReport report = await h.PassAsync(session);

        report.Should().Be(new RestoredVideosReport(1, 1, 0, 0, 0));
        session.RawReads.Should().Equal("public/ad-pour-warm-light.mp4");
        session.Files["public/ad-pour-warm-light.mp4"].Should().Equal(Harness.CopyBytes, "the copy replaces the original under the same name");
        h.FfmpegRuns.Should().ContainSingle().Which.Should().ContainInOrder("-fps_mode", "passthrough");
        h.Store.Keys.Should().Contain(k => k.StartsWith($"projects/{ProjectId}/agentFiles/render-proxies/", StringComparison.Ordinal) && k.EndsWith(".mp4", StringComparison.Ordinal));

        JsonElement record = session.Record("public/ad-pour-warm-light.mp4");
        record.GetProperty("proxied").GetBoolean().Should().BeTrue();
        record.GetProperty("width").GetInt32().Should().Be(1080);
        record.GetProperty("height").GetInt32().Should().Be(1920);
        record.GetProperty("sourceWidth").GetInt32().Should().Be(2160);
        record.GetProperty("sourceHeight").GetInt32().Should().Be(3840);
    }

    [Fact]
    public async Task The_same_unmapped_video_in_a_second_restore_reuses_the_cached_copy()
    {
        Harness h = new();
        await h.PassAsync(h.RestoredSession(("public/clip.mp4", "sandbox-clip", 2160, 3840)));
        WorkspaceSession again = h.RestoredSession(("public/clip.mp4", "sandbox-clip", 2160, 3840));

        RestoredVideosReport report = await h.PassAsync(again);

        report.Copied.Should().Be(1);
        h.FfmpegRuns.Should().ContainSingle("the copy is cached by the content's SHA-256");
        again.Files["public/clip.mp4"].Should().Equal(Harness.CopyBytes);
    }

    [Fact]
    public async Task A_video_mapped_to_a_project_file_comes_from_the_proxy_cache_without_reading_the_sandbox()
    {
        Harness h = new();
        ProjectWorkspaceFile clip = h.AddFile("ad-pour-mint-shirt.mp4", 1440, 2560);
        await h.StageAsync(clip);
        h.FfmpegRuns.Should().ContainSingle();
        int probes = h.Probes;

        // The workspace a revision restores: the original under public/, captured before copies existed.
        WorkspaceSession session = new();
        session.Files["public/ad-pour-mint-shirt.mp4"] = Harness.SourceBytes(clip.StorageKey);

        RestoredVideosReport report = await h.PassAsync(session);

        report.Copied.Should().Be(1);
        session.RawReads.Should().BeEmpty("a mapped video never travels out of the sandbox");
        h.FfmpegRuns.Should().ContainSingle("the copy made at staging is reused");
        h.Probes.Should().Be(probes, "the cached facts answer");
        session.Files["public/ad-pour-mint-shirt.mp4"].Should().Equal(Harness.CopyBytes);
        JsonElement record = session.Record("public/ad-pour-mint-shirt.mp4");
        record.GetProperty("proxied").GetBoolean().Should().BeTrue();
        record.GetProperty("sourceKey").GetString().Should().Be(clip.StorageKey);
    }

    [Fact]
    public async Task A_small_video_is_left_as_it_is_and_recorded_once()
    {
        Harness h = new();
        WorkspaceSession session = h.RestoredSession(("public/small.mp4", "sandbox-small", 1080, 1920));
        byte[] original = session.Files["public/small.mp4"];

        RestoredVideosReport report = await h.PassAsync(session);

        report.Should().Be(new RestoredVideosReport(1, 0, 1, 0, 0));
        session.Files["public/small.mp4"].Should().Equal(original);
        session.ByteWrites.Should().BeEmpty();
        h.FfmpegRuns.Should().BeEmpty();
        session.Record("public/small.mp4").GetProperty("proxied").GetBoolean().Should().BeFalse();

        (await h.PassAsync(session)).Examined.Should().Be(0, "a recorded video within size is not looked at again");
    }

    [Fact]
    public async Task A_recorded_copy_and_a_non_mp4_video_are_not_looked_at()
    {
        Harness h = new();
        WorkspaceSession session = h.RestoredSession(("public/a.webm", "sandbox-webm", 3840, 2160));
        session.Files[ReactRemotionSandboxTools.StagedVideosManifestPath] = Encoding.UTF8.GetBytes(
            "{\"videos\":{\"public/b.mp4\":{\"path\":\"public/b.mp4\",\"width\":1080,\"height\":1920,\"fpsNum\":30,\"fpsDen\":1,\"proxied\":true,\"sourceWidth\":2160,\"sourceHeight\":3840}}}");
        session.Files["public/b.mp4"] = Harness.CopyBytes;

        RestoredVideosReport report = await h.PassAsync(session);

        report.Examined.Should().Be(0);
        session.RawReads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_leaves_the_original_and_never_throws()
    {
        Harness h = new() { ProbeFails = true };
        WorkspaceSession session = h.RestoredSession(("public/big.mp4", "sandbox-big", 2160, 3840));
        byte[] original = session.Files["public/big.mp4"];

        RestoredVideosReport report = await h.PassAsync(session);

        report.Failed.Should().Be(1);
        session.Files["public/big.mp4"].Should().Equal(original);
        session.ByteWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_copy_keeps_the_original_and_records_why()
    {
        Harness h = new() { FfmpegFails = true };
        WorkspaceSession session = h.RestoredSession(("public/big.mp4", "sandbox-big", 2160, 3840));
        byte[] original = session.Files["public/big.mp4"];

        RestoredVideosReport report = await h.PassAsync(session);

        report.Copied.Should().Be(0);
        session.Files["public/big.mp4"].Should().Equal(original);
        JsonElement record = session.Record("public/big.mp4");
        record.GetProperty("proxied").GetBoolean().Should().BeFalse();
        record.GetProperty("width").GetInt32().Should().Be(2160);
        record.GetProperty("reason").GetString().Should().Be("copy_failed");
    }

    [Fact]
    public async Task A_pass_stops_at_its_video_bound()
    {
        Harness h = new();
        WorkspaceSession session = h.RestoredSession([.. Enumerable.Range(0, ReactRemotionSandboxTools.MaxRestoredVideosPerPass + 2)
            .Select(i => ($"public/c{i:D2}.mp4", $"sandbox-c{i}", 1080, 1920))]);

        RestoredVideosReport report = await h.PassAsync(session);

        report.Examined.Should().Be(ReactRemotionSandboxTools.MaxRestoredVideosPerPass);
        report.Skipped.Should().Be(2);
    }

    [Fact]
    public void Only_a_listed_project_file_is_ever_mapped()
    {
        string key = $"projects/{ProjectId}/userFiles/a.mp4";
        List<ProjectWorkspaceFile> files =
        [
            new(Guid.NewGuid(), ProjectId, "a.mp4", null, "userFiles", key, "video/mp4", 500, DateTime.UtcNow, null),
            new(Guid.NewGuid(), ProjectId, "twin.mp4", null, "userFiles", $"projects/{ProjectId}/userFiles/1/twin.mp4", "video/mp4", 9, DateTime.UtcNow, null),
            new(Guid.NewGuid(), ProjectId, "twin.mp4", null, "userFiles", $"projects/{ProjectId}/userFiles/2/twin.mp4", "video/mp4", 9, DateTime.UtcNow, null),
        ];
        ReactRemotionSandboxTools.StagedVideoFacts forged = new("public/x.mp4", 3840, 2160, 30, 1, false, 3840, 2160,
            SourceKey: $"projects/{ProjectId}/quarantine/x.mp4");

        ReactRemotionSandboxTools.MapRestoredVideo(files, null, "public/clips/a.mp4", 500, ProjectId).Should().Be(key);
        ReactRemotionSandboxTools.MapRestoredVideo(files, null, "public/a.mp4", 501, ProjectId).Should().BeNull("the size must match");
        ReactRemotionSandboxTools.MapRestoredVideo(files, null, "public/twin.mp4", 9, ProjectId).Should().BeNull("two candidates are ambiguous");
        ReactRemotionSandboxTools.MapRestoredVideo(files, forged, "public/x.mp4", 7, ProjectId).Should().BeNull("a record cannot name an object that is not a listed file");
        ReactRemotionSandboxTools.MapRestoredVideo(files, forged with { SourceKey = key }, "public/x.mp4", 7, ProjectId).Should().Be(key);
    }

    [Fact]
    public void A_record_is_looked_at_again_only_when_it_is_an_oversized_original_that_could_be_copied()
    {
        ReactRemotionSandboxTools.StagedVideoFacts original = new("public/a.mp4", 3840, 2160, 30, 1, false, 3840, 2160);

        ReactRemotionSandboxTools.NeedsDeliveryCheck("public/a.mp4", null, 1920).Should().BeTrue();
        ReactRemotionSandboxTools.NeedsDeliveryCheck("public/a.webm", null, 1920).Should().BeFalse();
        ReactRemotionSandboxTools.NeedsDeliveryCheck("public/a.mp4", original, 1920).Should().BeTrue("a 2160p run kept it; this one does not");
        ReactRemotionSandboxTools.NeedsDeliveryCheck("public/a.mp4", original, 3840).Should().BeFalse();
        ReactRemotionSandboxTools.NeedsDeliveryCheck("public/a.mp4", original with { Reason = StagedVideoProxy.Reasons.HasAlpha }, 1920).Should().BeFalse();
        ReactRemotionSandboxTools.NeedsDeliveryCheck("public/a.mp4", original with { Proxied = true, Width = 1920, Height = 1080 }, 1920).Should().BeFalse();
    }

    [Fact]
    public void The_cache_budget_counts_an_unrecorded_large_video_as_4k()
    {
        ReactRemotionSandboxTools.FrameBytesFor(null, 10L * 1024 * 1024).Should().Be(ReactRemotionSandboxTools.UnknownVideoFrameBytes);
        ReactRemotionSandboxTools.FrameBytesFor(null, 80L * 1024 * 1024).Should().Be(3840L * 2160 * 4);
        ReactRemotionSandboxTools.FrameBytesFor(new("public/a.mp4", 1080, 1920, 30, 1, true, 2160, 3840), 80L * 1024 * 1024)
            .Should().Be(1080L * 1920 * 4, "a record wins over the guess");
    }

    // --- The restorer calls the pass after every import ---------------------------------------------

    [Fact]
    public async Task The_restorer_prepares_the_videos_after_the_import_and_a_failure_never_fails_the_restore()
    {
        using CompositionHarness c = new();
        Guid origin = Guid.NewGuid();
        await c.SeedExecutionAsync(origin, ProjectId);
        FakeWorkspaceSession source = new(origin);
        source.Files["src/root.tsx"] = Encoding.UTF8.GetBytes("export const Root = () => null;");
        source.Files["public/clip.mp4"] = [1, 2, 3];
        CompositionWorkspaceVersion v1 = (await c.Capture.CaptureAsync(source, new CompositionCaptureRequest
        {
            ProjectId = ProjectId,
            OrganizationId = Guid.NewGuid(),
            ExecutionId = origin,
            CompositionId = "FinalVideo",
            RenderOutputStorageKey = "projects/p/outputFiles/x/promo.mp4"
        }, CancellationToken.None)).Version!;

        Mock<IRestoredWorkspaceVideos> videos = new();
        FakeWorkspaceSession fresh = new(Guid.NewGuid());
        bool importedFirst = false;
        videos.Setup(v => v.PrepareRestoredVideosAsync(fresh, ProjectId, It.IsAny<CancellationToken>()))
            .Callback(() => importedFirst = fresh.Files.ContainsKey("public/clip.mp4"))
            .ThrowsAsync(new InvalidOperationException("boom"));
        CompositionWorkspaceRestorer restorer = new(c.Store, c.Configuration, NullLogger<CompositionWorkspaceRestorer>.Instance, videos.Object);

        CompositionRestoreResult restored = await restorer.RestoreAsync(fresh, v1, CancellationToken.None);

        restored.Files.Should().Be(2);
        importedFirst.Should().BeTrue("the videos are prepared after the import, before anything can render");
        videos.Verify(v => v.PrepareRestoredVideosAsync(fresh, ProjectId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- REVISION_RENDER_FAILED names the clip ----------------------------------------------------------

    [Fact]
    public void A_failed_render_names_the_video_it_could_not_read()
    {
        string result = JsonSerializer.Serialize(new
        {
            success = false,
            error = "execution failed: exit status 1",
            outputTail = "Error: Could not extract frame from compositor",
            videoHint = "Remotion could not read a frame of public/ad-pour-warm-light.mp4 (2160x3840). ...",
            failingVideos = new[] { new { path = "public/ad-pour-warm-light.mp4", sizeBytes = 91234567, width = 2160, height = 3840 } }
        });

        string text = ReactRemotionSandboxTools.RenderErrorText(result);

        text.Should().Be("execution failed: exit status 1. Remotion could not read a frame of public/ad-pour-warm-light.mp4 (2160x3840).");
    }

    [Fact]
    public void A_bare_exit_status_carries_the_last_error_line_without_links_or_newlines()
    {
        string result = JsonSerializer.Serialize(new
        {
            success = false,
            error = "execution failed: exit status 1",
            outputTail = "Rendered 92/300\nError: delayRender() timed out for http://localhost:3000/proxy?src=%2Fpublic%2Fx.mp4&time=3.1\n    at x (y.js:1)"
        });

        string text = ReactRemotionSandboxTools.RenderErrorText(result);

        text.Should().Be("execution failed: exit status 1. Remotion said: Error: delayRender() timed out for [link]");
    }

    // --- Harness ---------------------------------------------------------------------------------

    private sealed class Harness
    {
        public static readonly byte[] CopyBytes = Encoding.ASCII.GetBytes("copy-1080");

        public static byte[] SourceBytes(string key) => Encoding.UTF8.GetBytes("source:" + key);

        private readonly List<ProjectWorkspaceFile> _files = [];
        private readonly Dictionary<string, (int W, int H)> _sizes = [];
        private readonly WorkflowExecutionContextAccessor _accessor = new();
        private readonly ReactRemotionSandboxTools _tool;
        private readonly SwitchingSessionFactory _sessions = new();

        public Dictionary<string, byte[]> Store { get; } = new(StringComparer.Ordinal);
        public List<IReadOnlyList<string>> FfmpegRuns { get; } = [];
        public int Probes { get; private set; }
        public bool FfmpegFails { get; init; }
        public bool ProbeFails { get; init; }

        public Harness()
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
                    if (ProbeFails)
                        return new VideoToolResult(1, "", "Invalid data found when processing input", false);
                    args.Should().ContainInOrder("-format_whitelist", MediaDerivativeInputFormats.Whitelist);
                    string path = args[^1];
                    if (path.EndsWith("copy.mp4", StringComparison.Ordinal))
                        return Ok(ProbeJson(1080, 1920));
                    string content = Encoding.UTF8.GetString(File.ReadAllBytes(path));
                    (int w, int h) = _sizes[content["source:".Length..]];
                    return Ok(ProbeJson(w, h));
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
            Mock<IProjectFileWorkspace> workspace = new();
            workspace.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(() => _files);

            _tool = new ReactRemotionSandboxTools(
                Mock.Of<IHttpClientFactory>(),
                _accessor,
                workspace.Object,
                s3.Object,
                configuration,
                NullLogger<ReactRemotionSandboxTools>.Instance,
                Mock.Of<IServiceScopeFactory>(),
                videoToolRunner: runner.Object,
                sandboxSessions: _sessions);
        }

        public ProjectWorkspaceFile AddFile(string name, int width, int height)
        {
            string key = $"projects/{ProjectId}/userFiles/{name}";
            ProjectWorkspaceFile file = new(Guid.NewGuid(), ProjectId, name, null, "userFiles", key, "video/mp4",
                SourceBytes(key).LongLength, DateTime.UtcNow, null);
            _files.Add(file);
            _sizes[key] = (width, height);
            Store[key] = SourceBytes(key);
            return file;
        }

        /// <summary>A restored sandbox holding these videos under <c>public/</c>, with no record and no project file behind them.</summary>
        public WorkspaceSession RestoredSession(params (string Path, string Identity, int Width, int Height)[] videos)
        {
            WorkspaceSession session = new();
            foreach ((string path, string identity, int width, int height) in videos)
            {
                _sizes[identity] = (width, height);
                session.Files[path] = SourceBytes(identity);
            }
            return session;
        }

        public async Task StageAsync(ProjectWorkspaceFile file)
        {
            WorkspaceSession session = new();
            _sessions.Current = session;
            using (_accessor.BeginScope(session.ExecutionId, ProjectId, "correlation", null))
            {
                await _tool.StageProjectFile(file.Id.ToString());
            }
        }

        public Task<RestoredVideosReport> PassAsync(WorkspaceSession session) =>
            ((IRestoredWorkspaceVideos)_tool).PrepareRestoredVideosAsync(session, ProjectId, CancellationToken.None);

        private static VideoToolResult Ok(string stdout) => new(0, stdout, "", false);

        private static string ProbeJson(int width, int height) =>
            "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":" + width + ",\"height\":" + height +
            ",\"r_frame_rate\":\"30/1\",\"avg_frame_rate\":\"30/1\",\"pix_fmt\":\"yuv420p\"}]," +
            "\"format\":{\"duration\":\"10.0\",\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\"}}";
    }

    private sealed class SwitchingSessionFactory : ISandboxSessionFactory
    {
        public ISandboxSession? Current { get; set; }

        public ISandboxSession Create(Guid executionId) => Current ?? throw new InvalidOperationException("no session");
    }

    /// <summary>A workspace as a dictionary of files: list, text, bytes and raw reads.</summary>
    private sealed class WorkspaceSession : ISandboxSession
    {
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> RawReads { get; } = [];
        public List<string> ByteWrites { get; } = [];

        public JsonElement Record(string path)
        {
            using JsonDocument manifest = JsonDocument.Parse(Files[ReactRemotionSandboxTools.StagedVideosManifestPath]);
            return manifest.RootElement.GetProperty("videos").GetProperty(path).Clone();
        }

        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
        {
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
            return entries.Count == 0
                ? throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "not found")
                : Task.FromResult<IReadOnlyList<SandboxFileEntry>>([.. entries.Values]);
        }

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            Files.TryGetValue(path, out byte[]? bytes)
                ? Task.FromResult(new SandboxTextFile(path, Encoding.UTF8.GetString(bytes)))
                : throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "not found");

        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            Files[path] = Encoding.UTF8.GetBytes(content);
            return Task.FromResult(new SandboxActionResult(true));
        }

        public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
        {
            ByteWrites.Add(path);
            Files[path] = content;
            return Task.CompletedTask;
        }

        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken)
        {
            RawReads.Add(path);
            byte[] bytes = Files[path];
            HttpClient client = new();
            HttpResponseMessage response = new(HttpStatusCode.OK);
            return Task.FromResult(new SandboxRawFile(client, response, new MemoryStream(bytes), bytes.LongLength));
        }

        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
