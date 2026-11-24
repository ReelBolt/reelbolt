using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// D11's write member on the media seam, pinned against the local write it replaced. The compile path
/// used to spell a session write "MaterializeAsync then File.WriteAllTextAsync"; D11 routes it through
/// <see cref="IMediaWorkspace.WriteTextAsync"/> so the same call also lands in a REMOTE session. That
/// is only safe if the local arm is byte-for-byte what the old spelling produced, which is what these
/// tests hold.
/// </summary>
public class MediaWorkspaceWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "d11-write-" + Guid.NewGuid().ToString("N"));

    public MediaWorkspaceWriteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory is disposable anyway.
        }
    }

    private LocalMediaWorkspace Workspace() => LocalMediaWorkspace.CreateAt(_root, new NoopRunner());

    private sealed class NoopRunner : IVideoToolRunner
    {
        public Task<VideoToolResult> RunFfmpegAsync(System.Collections.Generic.IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            System.Collections.Generic.IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct, onStdOutLine);

        public Task<VideoToolResult> RunFfprobeAsync(System.Collections.Generic.IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfprobeAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(System.Collections.Generic.IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<VideoToolResult> RunFfmpegAsync(System.Collections.Generic.IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
            => Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<VideoToolResult> RunFfprobeAsync(System.Collections.Generic.IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, System.Collections.Generic.IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => workspace.RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, System.Collections.Generic.IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
            Action<string> onStdOutLine)
            => workspace.RunFfmpegAsync(args, timeout, ct, onStdOutLine);
    }

    [Fact]
    public async Task Utf8NoBomWritesExactlyWhatFileWriteAllTextWrites()
    {
        const string Content = "drawtext=textfile=caption-1.txt:fontcolor=white\nline two";
        LocalMediaWorkspace workspace = Workspace();

        await workspace.WriteTextAsync("caption-1.txt", Content, MediaWorkspaceTexts.Utf8NoBom, CancellationToken.None);

        byte[] viaSeam = await File.ReadAllBytesAsync(Path.Combine(_root, "caption-1.txt"));
        byte[] viaFileApi = Encoding.UTF8.GetBytes(Content);
        viaSeam.Should().Equal(viaFileApi, "File.WriteAllTextAsync(path, text) with no encoding is UTF-8 with NO byte-order mark");
        viaSeam.Length.Should().Be(viaFileApi.Length);
        viaSeam[0].Should().Be((byte)'d', "a byte-order mark would have pushed the first byte of the text out of position");
    }

    [Fact]
    public async Task EncodingUtf8WritesTheByteOrderMarkFileWriteAllTextWithThatEncodingWrites()
    {
        LocalMediaWorkspace workspace = Workspace();

        await workspace.WriteTextAsync("subs.ass", "[Script Info]", Encoding.UTF8, CancellationToken.None);

        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(_root, "subs.ass"));
        bytes.Should().StartWith(new byte[] { 0xEF, 0xBB, 0xBF }, "Encoding.UTF8 carries a preamble, and StreamWriter writes it");
    }

    [Fact]
    public async Task AWriteCreatesTheDirectoriesOnTheWayLikeMaterializeThenWriteDid()
    {
        LocalMediaWorkspace workspace = Workspace();

        await workspace.WriteBytesAsync("nested/deeper/filter.txt", Encoding.UTF8.GetBytes("x"), CancellationToken.None);

        File.Exists(Path.Combine(_root, "nested", "deeper", "filter.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task AWriteStaysInsideTheSessionAndRefusesAPathThatEscapes()
    {
        // The same containment every other virtual path goes through, applied to the write as well:
        // this is the call that used to be a plain File.WriteAllText on a path MaterializeAsync returned.
        LocalMediaWorkspace workspace = Workspace();

        Func<Task> traversal = () => workspace.WriteTextAsync("../escaped.txt", "x", MediaWorkspaceTexts.Utf8NoBom, CancellationToken.None);
        Func<Task> absolute = () => workspace.WriteTextAsync("/tmp/escaped.txt", "x", MediaWorkspaceTexts.Utf8NoBom, CancellationToken.None);

        await traversal.Should().ThrowAsync<Exception>();
        await absolute.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TheDocumentUploadPathUsesTheKeyTheObjectStoreReturnsWithoutProvingTheRoundTrip()
    {
        // D11 splits the two uploads deliberately. The MEDIA publish (the compiled program) fails when
        // the object store lands the object somewhere else, because the caller registers that key as
        // the program's location. The DOCUMENT upload (the engine's EDL and timeline manifest) keeps
        // the ordinary artifact contract - pass the key you want, use what comes back - so routing it
        // through the seam did not change what any existing call site observes.
        FakeProjectFileWorkspace files = new();
        LocalMediaWorkspace workspace = LocalMediaWorkspace.Create(
            VideoScratchSpace.Create(new VideoEditingOptions { ScratchPath = _root }, Guid.NewGuid(), Guid.NewGuid()),
            new NoopRunner(),
            files,
            Guid.Parse("90000000-0000-4000-8000-000000000001"));

        await workspace.WriteTextAsync("edl.json", "{}", MediaWorkspaceTexts.Utf8NoBom, CancellationToken.None);

        string returned = await workspace.UploadArtifactAsync(
            "edl.json",
            "projects/90000000-0000-4000-8000-000000000001/agentFiles/video-analysis/e/step-3-edl.json",
            "application/json",
            CancellationToken.None);

        returned.Should().Be("projects/90000000-0000-4000-8000-000000000001/agentFiles/video-analysis/e/step-3-edl.json");
        files.Uploads.Should().ContainSingle();
    }

    [Fact]
    public async Task AWriteOverwritesRatherThanAppending()
    {
        LocalMediaWorkspace workspace = Workspace();

        await workspace.WriteTextAsync("script.txt", "first", MediaWorkspaceTexts.Utf8NoBom, CancellationToken.None);
        await workspace.WriteTextAsync("script.txt", "second", MediaWorkspaceTexts.Utf8NoBom, CancellationToken.None);

        (await File.ReadAllTextAsync(Path.Combine(_root, "script.txt"))).Should().Be("second");
    }
}
