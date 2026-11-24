using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>
/// #1206: on a remote (runner) session <see cref="IMediaWorkspace.MaterializeAsync"/> returns the session-relative
/// name unchanged, so the gray frames and the beat PCM must be read through the workspace, never off the engine's disk.
/// </summary>
public class RemoteSessionEventReadTests
{
    private sealed class NullConsumer : IFrameConsumer
    {
        public int Frames { get; private set; }

        public void OnFrame(int index, ReadOnlySpan<byte> gray) => Frames++;
    }

    /// <summary>A remote-kind session: virtual paths come back unchanged and the session's files live only in memory.</summary>
    private static Mock<IMediaWorkspace> RemoteSession(Dictionary<string, byte[]> files, Func<string, byte[]> produce)
    {
        var workspace = new Mock<IMediaWorkspace>();
        workspace.SetupGet(w => w.IsRemoteSession).Returns(true);
        workspace
            .Setup(w => w.MaterializeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, CancellationToken _) => p);
        workspace
            .Setup(w => w.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "avg_frame_rate=30/1\nr_frame_rate=30/1\n", string.Empty, false));
        workspace
            .Setup(w => w.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> argv, TimeSpan _, CancellationToken _) =>
            {
                string output = argv[^1];
                files[output] = produce(output);
                return new VideoToolResult(0, string.Empty, string.Empty, false);
            });
        workspace
            .Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, int max, CancellationToken _) =>
            {
                byte[] bytes = files[p];
                if (bytes.Length > max)
                    throw new MediaWorkspaceFileTooLargeException(p, bytes.Length, max);
                return bytes;
            });
        return workspace;
    }

    private static IOptions<VideoEditingOptions> Options() => Microsoft.Extensions.Options.Options.Create(new VideoEditingOptions());

    [Fact]
    public async Task Gray_frames_on_a_remote_session_are_read_through_the_workspace()
    {
        var files = new Dictionary<string, byte[]>();
        Mock<IMediaWorkspace> workspace = RemoteSession(files, _ => new byte[FrameSignalSampler.FrameBytes * 3]);
        var sampler = new FrameSignalSampler(Options(), NullLogger<FrameSignalSampler>.Instance);
        var consumer = new NullConsumer();

        FrameSignalInfo info = await sampler.SampleAsync(workspace.Object, "source.mp4", 30, 1, 10, consumer, CancellationToken.None);

        info.FrameCount.Should().Be(3);
        consumer.Frames.Should().Be(3);
        workspace.Verify(w => w.ReadAsync(It.Is<string>(p => p.StartsWith("events-gray-")), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Beat_pcm_on_a_remote_session_is_read_through_the_workspace()
    {
        var files = new Dictionary<string, byte[]>();
        Mock<IMediaWorkspace> workspace = RemoteSession(files, output => output.StartsWith("events-pcm-")
            ? new byte[SpectralFluxBeatDetector.SampleRate * 2 * 2]
            : new byte[FrameSignalSampler.FrameBytes * 3]);
        var sampler = new FrameSignalSampler(Options(), NullLogger<FrameSignalSampler>.Instance);
        var detector = new VideoEventDetector(sampler, new Mock<ISilenceDetector>().Object, Options(), NullLogger<VideoEventDetector>.Instance);
        var probe = new MediaProbeResult(2.0, 30, 1, 320, 180, "h264", "aac", null, "yuv420p");

        VideoEventSet set = await detector.DetectAsync(
            workspace.Object, "source.mp4", probe, new VideoEventOptions { DetectSpeech = false, DetectBeats = true }, ct: CancellationToken.None);

        set.Degraded.Should().BeFalse("the beat PCM was read through the session, not off the engine's disk");
        workspace.Verify(w => w.ReadAsync(It.Is<string>(p => p.StartsWith("events-pcm-")), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        files.Keys.Should().Contain(k => k.StartsWith("events-pcm-"));
    }
}
