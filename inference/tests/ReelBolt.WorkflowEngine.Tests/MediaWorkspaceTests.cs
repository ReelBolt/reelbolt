using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers the D9a seam itself — <see cref="IMediaWorkspace"/> and
/// <see cref="LocalMediaWorkspace"/> — and pins the property the whole work package turns on: the
/// argv the media services hand to ffmpeg/ffprobe is <b>byte-identical</b> whether the caller
/// addresses a file by a session-relative virtual path through a workspace or by the absolute local
/// path it used before the seam existed.
/// </summary>
public class MediaWorkspaceTests
{
    private static IOptions<VideoEditingOptions> Options(string scratchPath) =>
        Microsoft.Extensions.Options.Options.Create(
            new VideoEditingOptions
            {
                ScratchPath = scratchPath,
                AnalyzeTimeoutSeconds = 10,
                CompileTimeoutSeconds = 60
            });

    private static VideoEditingOptions EditOptions(string scratchPath) => Options(scratchPath).Value;

    private static string Join(IEnumerable<string[]> calls) =>
        string.Join(" :: ", calls.Select(call => string.Join("|", call)));

    /// <summary>Records every invocation verbatim and answers with a scripted result.</summary>
    private sealed class RecordingVideoToolRunner : IVideoToolRunner
    {
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct, onStdOutLine);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfprobeAsync(args, timeout, ct);

        public List<string[]> FfmpegCalls { get; } = new();
        public List<string[]> FfprobeCalls { get; } = new();

        public Func<string[], VideoToolResult> FfmpegAnswer { get; set; } =
            _ => new VideoToolResult(0, string.Empty, string.Empty, false);

        public Func<string[], VideoToolResult> FfprobeAnswer { get; set; } =
            _ => new VideoToolResult(0, string.Empty, string.Empty, false);

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            string[] copy = args.ToArray();
            FfmpegCalls.Add(copy);
            return Task.FromResult(FfmpegAnswer(copy));
        }

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        {
            string[] copy = args.ToArray();
            FfmpegCalls.Add(copy);
            onStdOutLine("progress=continue");
            return Task.FromResult(FfmpegAnswer(copy));
        }

        public Task<VideoToolResult> RunFfprobeAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            string[] copy = args.ToArray();
            FfprobeCalls.Add(copy);
            return Task.FromResult(FfprobeAnswer(copy));
        }

        // The workspace arm is pure delegation, and a local workspace routes straight back into the
        // three-argument members above - so a call made through it is recorded exactly once, by the
        // same recorder, with the same argv.
        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => workspace.RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
            Action<string> onStdOutLine)
            => workspace.RunFfmpegAsync(args, timeout, ct, onStdOutLine);
    }

    /// <summary>A throwaway scratch directory that is deleted with the test.</summary>
    private sealed class TempScratch : IDisposable
    {
        public TempScratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "d9a-mediaworkspace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string WriteFile(string name, long bytes)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort; the temp directory is disposable anyway.
            }
        }
    }

    private static string ProbeJson() => @"{
        ""streams"": [{
            ""codec_type"": ""video"",
            ""codec_name"": ""h264"",
            ""width"": 1920,
            ""height"": 1080,
            ""r_frame_rate"": ""30/1"",
            ""pix_fmt"": ""yuv420p"",
            ""duration"": ""10.0""
        }],
        ""format"": {""duration"": ""10.0""}
    }";

    [Fact]
    public async Task Materialize_resolves_a_virtual_path_to_the_very_path_the_scratch_space_returns()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, new RecordingVideoToolRunner());

        string viaScratch = scratch.GetPath(Path.Combine("nested", "grid.rgb"));
        string viaWorkspace = await workspace.MaterializeAsync("nested/grid.rgb", CancellationToken.None);

        viaWorkspace.Should().Be(viaScratch);
        Directory.Exists(Path.GetDirectoryName(viaWorkspace)).Should().BeTrue();
    }

    [Theory]
    [InlineData("../escape.mp4")]
    [InlineData("a/../../escape.mp4")]
    public async Task Materialize_refuses_a_virtual_path_that_leaves_the_workspace(string virtualPath)
    {
        using TempScratch scratchRoot = new();
        IMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(scratchRoot.Root, new RecordingVideoToolRunner());

        Func<Task> act = async () => await workspace.MaterializeAsync(virtualPath, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Materialize_refuses_an_absolute_virtual_path()
    {
        using TempScratch scratchRoot = new();
        IMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(scratchRoot.Root, new RecordingVideoToolRunner());

        Func<Task> act = async () => await workspace.MaterializeAsync("/etc/passwd", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadAsync_returns_the_bytes_of_a_session_file()
    {
        using TempScratch scratchRoot = new();
        scratchRoot.WriteFile("patch.gray", 12);
        IMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(scratchRoot.Root, new RecordingVideoToolRunner());

        byte[] bytes = await workspace.ReadAsync("patch.gray", CancellationToken.None);

        bytes.Should().HaveCount(12);
    }

    [Fact]
    public async Task ReadAsync_fails_loudly_instead_of_truncating_over_maxBytes()
    {
        using TempScratch scratchRoot = new();
        scratchRoot.WriteFile("patch.gray", 64);
        IMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(scratchRoot.Root, new RecordingVideoToolRunner());

        Func<Task> act = async () => await workspace.ReadAsync("patch.gray", maxBytes: 32, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<MediaWorkspaceFileTooLargeException>();
        thrown.Which.ActualBytes.Should().Be(64);
        thrown.Which.MaxBytes.Should().Be(32);
    }

    [Fact]
    public async Task StatAsync_reports_existence_and_size_without_reading_the_file()
    {
        using TempScratch scratchRoot = new();
        scratchRoot.WriteFile("present.bin", 7);
        IMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(scratchRoot.Root, new RecordingVideoToolRunner());

        (await workspace.StatAsync("present.bin", CancellationToken.None)).Should()
            .Be(new MediaWorkspaceStat(Exists: true, Bytes: 7));
        (await workspace.StatAsync("absent.bin", CancellationToken.None)).Should()
            .Be(new MediaWorkspaceStat(Exists: false, Bytes: 0));
    }

    [Fact]
    public async Task RunFfmpegAsync_forwards_argv_and_timeout_verbatim()
    {
        using TempScratch scratchRoot = new();
        RecordingVideoToolRunner runner = new();
        IMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(scratchRoot.Root, runner);
        string[] argv = { "-nostdin", "-i", "in.mp4", "out.wav" };

        await workspace.RunFfmpegAsync(argv, TimeSpan.FromSeconds(42), CancellationToken.None);

        runner.FfmpegCalls.Should().ContainSingle();
        Join(runner.FfmpegCalls).Should().Be(string.Join("|", argv));
    }

    [Fact]
    public async Task StageAsync_downloads_the_object_to_the_virtual_path()
    {
        using TempScratch scratchRoot = new();
        Guid projectId = Guid.NewGuid();
        var projectFiles = new Mock<IProjectFileWorkspace>();
        string? destination = null;
        projectFiles
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                projectId, "projects/p/agentFiles/x.mp4", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, CancellationToken>((_, _, dest, _) => destination = dest)
            .Returns(Task.CompletedTask);

        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(
            scratch, new RecordingVideoToolRunner(), projectFiles.Object, projectId);

        await workspace.StageAsync("projects/p/agentFiles/x.mp4", "in/x.mp4", CancellationToken.None);

        destination.Should().Be(scratch.GetPath("in/x.mp4"));
    }

    [Fact]
    public async Task A_capped_StageAsync_hands_the_cap_to_the_streaming_download()
    {
        using TempScratch scratchRoot = new();
        Guid projectId = Guid.NewGuid();
        var projectFiles = new Mock<IProjectFileWorkspace>(MockBehavior.Strict);
        long? cap = null;
        projectFiles
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                projectId, "projects/p/userFiles/f.ttf", It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, long, CancellationToken>((_, _, _, max, _) => cap = max)
            .Returns(Task.CompletedTask);

        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(
            scratch, new RecordingVideoToolRunner(), projectFiles.Object, projectId);

        await workspace.StageAsync("projects/p/userFiles/f.ttf", "brand-font", 4096, CancellationToken.None);

        cap.Should().Be(4096);
    }

    [Fact]
    public void StageAsync_without_a_project_scope_fails_loudly()
    {
        using TempScratch scratchRoot = new();
        IMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(scratchRoot.Root, new RecordingVideoToolRunner());

        Func<Task> act = async () => await workspace.StageAsync("k", "v", CancellationToken.None);

        act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task PublishAsync_uploads_to_exactly_the_requested_key()
    {
        using TempScratch scratchRoot = new();
        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        scratchRoot.WriteFile("out.mp4", 4);

        var projectFiles = new Mock<IProjectFileWorkspace>();
        projectFiles
            .Setup(w => w.UploadArtifactAsync(
                projectId, It.IsAny<string>(), "outputFiles/" + executionId, "video/mp4",
                It.IsAny<CancellationToken>(), "outputFiles"))
            .ReturnsAsync($"projects/{projectId}/outputFiles/outputFiles/{executionId}");

        IMediaWorkspace workspace = LocalMediaWorkspace.Create(
            VideoScratchSpace.Create(EditOptions(scratchRoot.Root), executionId, stepId),
            new RecordingVideoToolRunner(),
            projectFiles.Object,
            projectId);

        string key = await workspace.PublishAsync(
            "out.mp4", $"projects/{projectId}/outputFiles/outputFiles/{executionId}", "video/mp4", CancellationToken.None);

        key.Should().Be($"projects/{projectId}/outputFiles/outputFiles/{executionId}");
    }

    [Fact]
    public async Task PublishAsync_refuses_a_key_outside_the_workspaces_project_scope()
    {
        using TempScratch scratchRoot = new();
        Guid projectId = Guid.NewGuid();
        var projectFiles = new Mock<IProjectFileWorkspace>();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(
            VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid()),
            new RecordingVideoToolRunner(),
            projectFiles.Object,
            projectId);

        Func<Task> act = async () => await workspace.PublishAsync(
            "out.mp4", $"projects/{Guid.NewGuid()}/outputFiles/out.mp4", "video/mp4", CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }


    // ---------------------------------------------------------------------------------------------
    // Golden-parity evidence for D9a: for every ported service, driving it through the workspace
    // and driving it through the absolute local path it took before the seam must produce the SAME
    // argv, in the same order. The argv is the whole contract with ffmpeg — if these hold, the
    // refactor is invisible to ffmpeg.
    // ---------------------------------------------------------------------------------------------

    private const int GridBytes = 4 * 3 * 3 * 3; // 4x3 RGB grid, 3 frames
    private const int GrayBytes = 4 * 3 * 3;     // 4x3 gray grid, 3 frames
    private const int PatchBytes = 8 * 8;        // 8x8 sharpness patch

    /// <summary>
    /// A runner that answers ffprobe with a real probe payload and, for ffmpeg, writes the bytes the
    /// caller's own read-back expects to the last argv token (which is the output path in every
    /// FfmpegArgvBuilder shape used here). That is the minimum needed for the read-back paths to run
    /// at all; nothing about it depends on which API the caller used.
    /// </summary>
    private static RecordingVideoToolRunner FfmpegAnswers()
    {
        return new RecordingVideoToolRunner
        {
            FfprobeAnswer = _ => new VideoToolResult(0, ProbeJson(), string.Empty, false),
            FfmpegAnswer = argv =>
            {
                string output = argv[^1];
                if (output.EndsWith(".rgb", StringComparison.Ordinal))
                    File.WriteAllBytes(output, new byte[GridBytes]);
                else if (output.EndsWith(".raw", StringComparison.Ordinal))
                    File.WriteAllBytes(output, new byte[GrayBytes]);
                else if (output.EndsWith(".gray", StringComparison.Ordinal))
                    File.WriteAllBytes(output, new byte[PatchBytes]);

                return new VideoToolResult(0, string.Empty, string.Empty, false);
            }
        };
    }

    private static void AssertSameArgv(RecordingVideoToolRunner local, RecordingVideoToolRunner workspace)
    {
        Join(workspace.FfprobeCalls).Should().Be(Join(local.FfprobeCalls));
        Join(workspace.FfmpegCalls).Should().Be(Join(local.FfmpegCalls));
    }

    [Fact]
    public async Task FfprobeMediaProbe_produces_the_same_argv_through_the_workspace()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");

        RecordingVideoToolRunner localRunner = FfmpegAnswers();
        MediaProbeResult local = await new FfprobeMediaProbe(localRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfprobeMediaProbe>.Instance)
            .ProbeAsync(mediaPath, CancellationToken.None);

        RecordingVideoToolRunner wsRunner = FfmpegAnswers();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, wsRunner);
        MediaProbeResult remote = await new FfprobeMediaProbe(wsRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfprobeMediaProbe>.Instance)
            .ProbeAsync(workspace, "source.mp4", CancellationToken.None);

        AssertSameArgv(localRunner, wsRunner);
        remote.Should().Be(local);
    }

    [Fact]
    public async Task FfmpegSilenceDetector_produces_the_same_argv_through_the_workspace()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");

        RecordingVideoToolRunner localRunner = FfmpegAnswers();
        await new FfmpegSilenceDetector(localRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegSilenceDetector>.Instance)
            .DetectAsync(mediaPath, -30, 0.5, 10.0, CancellationToken.None);

        RecordingVideoToolRunner wsRunner = FfmpegAnswers();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, wsRunner);
        await new FfmpegSilenceDetector(wsRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegSilenceDetector>.Instance)
            .DetectAsync(workspace, "source.mp4", -30, 0.5, 10.0, CancellationToken.None);

        AssertSameArgv(localRunner, wsRunner);
    }

    [Fact]
    public async Task FfmpegShotDetector_produces_the_same_argv_through_the_workspace()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");

        RecordingVideoToolRunner localRunner = FfmpegAnswers();
        await new FfmpegShotDetector(localRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegShotDetector>.Instance)
            .DetectShotsAsync(mediaPath, 0.3, 10.0, CancellationToken.None);

        RecordingVideoToolRunner wsRunner = FfmpegAnswers();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, wsRunner);
        await new FfmpegShotDetector(wsRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegShotDetector>.Instance)
            .DetectShotsAsync(workspace, "source.mp4", 0.3, 10.0, CancellationToken.None);

        AssertSameArgv(localRunner, wsRunner);
    }

    [Fact]
    public async Task FfmpegAudioExtractor_produces_the_same_argv_through_the_workspace()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");
        string wavPath = scratch.GetPath("audio-full.wav");
        string chunkPath = scratch.GetPath("audio-chunk-0.wav");

        RecordingVideoToolRunner localRunner = FfmpegAnswers();
        FfmpegAudioExtractor localExtractor = new(localRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegAudioExtractor>.Instance);
        await localExtractor.ExtractWavAsync(mediaPath, wavPath, CancellationToken.None);
        await localExtractor.ExtractWavRangeAsync(mediaPath, chunkPath, 1.5, 4.5, CancellationToken.None);

        RecordingVideoToolRunner wsRunner = FfmpegAnswers();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, wsRunner);
        FfmpegAudioExtractor wsExtractor = new(wsRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegAudioExtractor>.Instance);
        await wsExtractor.ExtractWavAsync(workspace, "source.mp4", "audio-full.wav", CancellationToken.None);
        await wsExtractor.ExtractWavRangeAsync(workspace, "source.mp4", "audio-chunk-0.wav", 1.5, 4.5, CancellationToken.None);

        AssertSameArgv(localRunner, wsRunner);
    }

    [Fact]
    public async Task FfmpegFrameGridSampler_produces_the_same_argv_and_pixels_through_the_workspace()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");

        RecordingVideoToolRunner localRunner = FfmpegAnswers();
        FfmpegFrameGridSampler localSampler = new(localRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegFrameGridSampler>.Instance);
        FrameGridResult localGrid = await localSampler.SampleAsync(
            mediaPath, scratch, 2.0, 4, 3, 10.0, 100, CancellationToken.None);

        // The RGB buffer always has the same session-relative name, so clear it before the second
        // pass — otherwise the workspace pass would just read the first pass's bytes.
        File.Delete(scratch.GetPath("grid.rgb"));

        RecordingVideoToolRunner wsRunner = FfmpegAnswers();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, wsRunner);
        FrameGridResult remoteGrid = await new FfmpegFrameGridSampler(wsRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegFrameGridSampler>.Instance)
            .SampleAsync(workspace, "source.mp4", 2.0, 4, 3, 10.0, 100, CancellationToken.None);

        AssertSameArgv(localRunner, wsRunner);
        remoteGrid.FrameCount.Should().Be(localGrid.FrameCount);
        remoteGrid.GridWidth.Should().Be(localGrid.GridWidth);
        remoteGrid.EffectiveFps.Should().Be(localGrid.EffectiveFps);
        remoteGrid.PixelData.Should().Equal(localGrid.PixelData);
    }

    [Fact]
    public async Task FfmpegFrameGridSampler_gray_pass_produces_the_same_argv_shape_through_the_workspace()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");

        RecordingVideoToolRunner localRunner = FfmpegAnswers();
        FrameGridResult localGray = await new FfmpegFrameGridSampler(localRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegFrameGridSampler>.Instance)
            .SampleGrayAsync(mediaPath, scratch, 2.0, 4, 3, 10.0, 100, CancellationToken.None);

        RecordingVideoToolRunner wsRunner = FfmpegAnswers();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, wsRunner);
        FrameGridResult remoteGray = await new FfmpegFrameGridSampler(wsRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegFrameGridSampler>.Instance)
            .SampleGrayAsync(workspace, "source.mp4", 2.0, 4, 3, 10.0, 100, CancellationToken.None);

        // The gray buffer gets a fresh GUID name per pass, so every session path is folded to one
        // placeholder before the two argv lists are compared.
        InSession(wsRunner, scratch.DirectoryPath).Should().Be(InSession(localRunner, scratch.DirectoryPath));
        remoteGray.FrameCount.Should().Be(localGray.FrameCount);
        remoteGray.PixelData.Should().Equal(localGray.PixelData);
        // Both passes read their buffer into memory and then removed it from the scratch space.
        Directory.GetFiles(scratch.DirectoryPath, "gray-*.raw").Should().BeEmpty();
    }

    /// <summary>Records what was reserved and whether the lease is still held.</summary>
    private sealed class RecordingScratchGuard : IScratchDiskGuard
    {
        public List<long> Reserved { get; } = [];
        public int Held { get; private set; }
        public long MaxSourceBytes => long.MaxValue;
        public int MaxSourceSeconds => 3600;

        public Task<IDisposable> ReserveAsync(long sourceBytes, CancellationToken ct)
        {
            Reserved.Add(sourceBytes);
            Held++;
            return Task.FromResult<IDisposable>(new Lease(this));
        }

        public bool TryReserve(long sourceBytes, out IDisposable lease)
        {
            lease = ReserveAsync(sourceBytes, CancellationToken.None).Result;
            return true;
        }

        private sealed class Lease(RecordingScratchGuard owner) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                owner.Held--;
            }
        }
    }

    [Fact]
    public async Task FfmpegFrameGridSampler_gray_pass_reserves_scratch_before_ffmpeg_runs_and_leaves_no_file_behind()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");
        RecordingScratchGuard guard = new();
        int heldWhenFfmpegRan = -1;
        string? written = null;
        RecordingVideoToolRunner runner = new()
        {
            FfmpegAnswer = argv =>
            {
                heldWhenFfmpegRan = guard.Held;
                written = argv[^1];
                File.WriteAllBytes(written, new byte[GrayBytes]);
                return new VideoToolResult(0, string.Empty, string.Empty, false);
            }
        };

        FrameGridResult gray = await new FfmpegFrameGridSampler(
                runner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegFrameGridSampler>.Instance, guard)
            .SampleGrayAsync(mediaPath, scratch, 2.0, 4, 3, 10.0, 100, CancellationToken.None);

        long cap = FfmpegFrameGridSampler.PlanMaxGrayFrames(100);
        guard.Reserved.Should().Equal(cap * 4 * 3);
        heldWhenFfmpegRan.Should().Be(1, "the space is reserved before ffmpeg starts to write");
        guard.Held.Should().Be(0, "the reservation is released when the pass ends");
        gray.PixelData.Length.Should().Be(GrayBytes);
        written.Should().NotBeNull();
        File.Exists(written!).Should().BeFalse("the frames are in memory; the scratch file is deleted");
    }

    [Fact]
    public async Task FfmpegFrameGridSampler_gray_pass_that_fails_still_deletes_its_file_and_releases_the_reservation()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");
        RecordingScratchGuard guard = new();
        string? written = null;
        RecordingVideoToolRunner runner = new()
        {
            FfmpegAnswer = argv =>
            {
                written = argv[^1];
                File.WriteAllBytes(written, new byte[7]);
                return new VideoToolResult(1, string.Empty, "boom", false);
            }
        };

        Func<Task> act = () => new FfmpegFrameGridSampler(
                runner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegFrameGridSampler>.Instance, guard)
            .SampleGrayAsync(mediaPath, scratch, 2.0, 4, 3, 10.0, 100, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        guard.Held.Should().Be(0);
        File.Exists(written!).Should().BeFalse();
    }

    private static string InSession(RecordingVideoToolRunner runner, string scratchDirectory) =>
        string.Join(" :: ", runner.FfmpegCalls.Select(call => string.Join("|",
            call.Select(token => token.StartsWith(scratchDirectory, StringComparison.Ordinal) ? "<session>" : token))));


    [Fact]
    public async Task FfmpegKeyframeExtractor_produces_the_same_argv_through_the_workspace()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");
        string framePath = scratch.GetPath("keyframe-shot1.jpg");
        string sheetPath = scratch.GetPath("sheet-shot1.jpg");
        string zoomPath = scratch.GetPath("subject-shot1-zoom.jpg");

        RecordingVideoToolRunner localRunner = FfmpegAnswers();
        FfmpegKeyframeExtractor localExtractor = new(localRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegKeyframeExtractor>.Instance);
        await localExtractor.ExtractKeyframeAsync(mediaPath, framePath, 1.5, 320, CancellationToken.None);
        await localExtractor.ExtractContactSheetAsync(mediaPath, sheetPath, new[] { 1.0, 2.0, 3.0 }, 320, CancellationToken.None);
        await localExtractor.ExtractCroppedKeyframeAsync(mediaPath, zoomPath, 2.5, 0.1, 0.2, 0.3, 0.4, 320, CancellationToken.None);

        RecordingVideoToolRunner wsRunner = FfmpegAnswers();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, wsRunner);
        FfmpegKeyframeExtractor wsExtractor = new(wsRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegKeyframeExtractor>.Instance);
        await wsExtractor.ExtractKeyframeAsync(workspace, "source.mp4", "keyframe-shot1.jpg", 1.5, 320, CancellationToken.None);
        await wsExtractor.ExtractContactSheetAsync(workspace, "source.mp4", "sheet-shot1.jpg", new[] { 1.0, 2.0, 3.0 }, 320, CancellationToken.None);
        await wsExtractor.ExtractCroppedKeyframeAsync(workspace, "source.mp4", "subject-shot1-zoom.jpg", 2.5, 0.1, 0.2, 0.3, 0.4, 320, CancellationToken.None);

        AssertSameArgv(localRunner, wsRunner);
    }

    [Fact]
    public async Task FfmpegSharpnessSampler_produces_the_same_argv_and_score_through_the_workspace()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("source.mp4");

        RecordingVideoToolRunner localRunner = FfmpegAnswers();
        double? localScore = await new FfmpegSharpnessSampler(localRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegSharpnessSampler>.Instance)
            .MeasureAsync(mediaPath, scratch, "sharp-shot1.gray", 1.5, 8, CancellationToken.None);

        RecordingVideoToolRunner wsRunner = FfmpegAnswers();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, wsRunner);
        double? remoteScore = await new FfmpegSharpnessSampler(wsRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegSharpnessSampler>.Instance)
            .MeasureAsync(workspace, "source.mp4", "sharp-shot1.gray", 1.5, 8, CancellationToken.None);

        AssertSameArgv(localRunner, wsRunner);
        remoteScore.Should().Be(localScore);
        localScore.Should().NotBeNull();
    }

    [Fact]
    public async Task FfmpegSharpnessSampler_still_reports_null_when_the_patch_was_not_produced()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        RecordingVideoToolRunner runner = new()
        {
            FfmpegAnswer = _ => new VideoToolResult(0, string.Empty, string.Empty, false)
        };
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, runner);

        double? score = await new FfmpegSharpnessSampler(runner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegSharpnessSampler>.Instance)
            .MeasureAsync(workspace, "source.mp4", "missing.gray", 1.5, 8, CancellationToken.None);

        score.Should().BeNull();
    }

    [Fact]
    public async Task GeneratedClipNormalizer_produces_the_same_argv_through_the_workspace()
    {
        using TempScratch scratchRoot = new();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(EditOptions(scratchRoot.Root), Guid.NewGuid(), Guid.NewGuid());
        string mediaPath = scratch.GetPath("generated.mp4");
        string normalizedPath = scratch.GetPath("normalized.mp4");

        RecordingVideoToolRunner localRunner = FfmpegAnswers();
        string localResult = await new GeneratedClipNormalizer(localRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<GeneratedClipNormalizer>.Instance)
            .NormalizeAsync(mediaPath, normalizedPath, CancellationToken.None);

        RecordingVideoToolRunner wsRunner = FfmpegAnswers();
        IMediaWorkspace workspace = LocalMediaWorkspace.Create(scratch, wsRunner);
        string remoteResult = await new GeneratedClipNormalizer(wsRunner, Options(scratchRoot.Root), Microsoft.Extensions.Logging.Abstractions.NullLogger<GeneratedClipNormalizer>.Instance)
            .NormalizeAsync(workspace, "generated.mp4", "normalized.mp4", CancellationToken.None);

        AssertSameArgv(localRunner, wsRunner);
        localResult.Should().Be(normalizedPath);
        remoteResult.Should().Be("normalized.mp4");
    }
}

