using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>#1206: the policy watermark is an engine-host file; a remote session gets its bytes written into the session.</summary>
public class RemoteSessionWatermarkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-watermark-tests", Guid.NewGuid().ToString("N"));

    public RemoteSessionWatermarkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (ResolvedOutputPolicy Policy, byte[] Bytes) Policy()
    {
        string path = Path.Combine(_root, "mark.PNG");
        byte[] bytes = [1, 2, 3, 4, 5];
        File.WriteAllBytes(path, bytes);
        return (new ResolvedOutputPolicy(720, "trial", true, true, path), bytes);
    }

    [Fact]
    public async Task A_remote_session_gets_the_watermark_bytes_and_a_session_path()
    {
        (ResolvedOutputPolicy policy, byte[] bytes) = Policy();
        var workspace = new Mock<IMediaWorkspace>();
        workspace.SetupGet(w => w.IsRemoteSession).Returns(true);
        workspace.Setup(w => w.MaterializeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, CancellationToken _) => p);

        ResolvedOutputPolicy? staged = await VideoCompileStepExecutor.StageWatermarkForSessionAsync(policy, workspace.Object, CancellationToken.None);

        staged!.WatermarkAssetPath.Should().Be("policy-watermark.png");
        staged.ApplyWatermark.Should().BeTrue();
        workspace.Verify(w => w.WriteBytesAsync("policy-watermark.png", bytes, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_local_session_keeps_the_host_path_untouched()
    {
        (ResolvedOutputPolicy policy, _) = Policy();
        var workspace = new Mock<IMediaWorkspace>(MockBehavior.Strict);
        workspace.SetupGet(w => w.IsRemoteSession).Returns(false);

        ResolvedOutputPolicy? staged = await VideoCompileStepExecutor.StageWatermarkForSessionAsync(policy, workspace.Object, CancellationToken.None);

        staged.Should().BeSameAs(policy);
    }
}
