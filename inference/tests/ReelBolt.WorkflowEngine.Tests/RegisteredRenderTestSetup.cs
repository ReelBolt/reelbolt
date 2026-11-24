using System;
using System.Threading;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A registered compile render is published through the media session to
/// <c>projects/{project}/outputFiles/{fileId}.mp4</c> and then registered from that key
/// (<see cref="IProjectFileWorkspace.RegisterStoredObjectAsync"/>). On a local session the publish is an
/// <see cref="IProjectFileWorkspace.UploadArtifactAsync"/> whose returned key must echo the requested one.
/// This adds both answers on top of a fixture's own setups (Moq matches the most recent setup first), so
/// a fixture's canned artifact key keeps answering its EDL/timeline uploads.
/// </summary>
internal static class RegisteredRenderTestSetup
{
    public static Mock<IProjectFileWorkspace> SetupRegisteredRenderPublish(
        this Mock<IProjectFileWorkspace> workspace,
        Action<string>? onPublishedLocalPath = null,
        Action<string>? onRegisteredFileName = null)
    {
        // A registered render's storage file name is "{fileId}.mp4" (no folder); the unregistered
        // publish is "{executionId}/{name}" and keeps whatever answer the fixture gave it.
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.Is<string>(n => !n.Contains('/')), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), "outputFiles"))
            .ReturnsAsync((Guid projectId, string localPath, string fileName, string _, CancellationToken _, string category) =>
            {
                onPublishedLocalPath?.Invoke(localPath);
                return $"projects/{projectId}/{category}/{fileName}";
            });
        workspace
            .Setup(w => w.RegisterStoredObjectAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid projectId, string key, string fileName, string mime, long size, SummaryStatus _,
                FileIndexingStatus _, CancellationToken _, string category) =>
            {
                onRegisteredFileName?.Invoke(fileName);
                return new ProjectWorkspaceFile(Guid.NewGuid(), projectId, fileName, null, category, key, mime, size, DateTime.UtcNow, null);
            });
        return workspace;
    }
}
