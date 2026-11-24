using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A speed-ramp graph over 4,000 characters goes to ffmpeg as a filter script. On a remote session the script
/// must be written INTO the session (where the runner's ffmpeg reads it), never to this host's disk under the
/// session-relative name MaterializeAsync answers.
/// </summary>
public sealed class SpeedRampApplierRemoteSessionTests
{
    [Fact]
    public async Task A_long_ramp_graph_is_written_into_the_remote_session_as_its_filter_script()
    {
        string root = Path.Combine(Path.GetTempPath(), "speed-ramp-remote-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var argvs = new List<IReadOnlyList<string>>();
            var runner = new Mock<IVideoToolRunner>();
            runner
                .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((a, _, _) => argvs.Add(a.ToList()))
                .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
            LocalMediaWorkspace session = LocalMediaWorkspace.CreateAt(root, runner.Object);
            var remote = new RemoteKindMediaWorkspace(session, []);

            // Many short ramps over a long program: well past the 4,000-character inline limit.
            RampTimeline timeline = RampTimeline.Build(
                600, Enumerable.Range(0, 60).Select(i => new RampWindow(5 + i * 9, 6 + i * 9, 0.5)).ToList());

            SpeedRampApplyResult result = await SpeedRampApplier.ApplyAsync(
                remote, "program.mp4", "program.mp4", timeline, hasAudio: true, 30, 1,
                "libx264", "aac", "veryfast", 20, TimeSpan.FromMinutes(1), CancellationToken.None);

            result.Succeeded.Should().BeTrue(result.Error);
            IReadOnlyList<string> argv = argvs.Should().ContainSingle().Which;
            int script = argv.ToList().IndexOf("-filter_complex_script");
            script.Should().BeGreaterThan(-1, "the graph is long enough to go through a script");
            (await session.StatAsync("speed_ramps_filter.txt", CancellationToken.None)).Exists
                .Should().BeTrue("the runner's ffmpeg reads the script from its own session");
            argv[script + 1].Should().Be(await session.MaterializeAsync("speed_ramps_filter.txt", CancellationToken.None),
                "the argv names the session's script, resolved by the runner");
        }
        finally
        {
            File.Delete("speed_ramps_filter.txt");
            Directory.Delete(root, recursive: true);
        }
    }
}
