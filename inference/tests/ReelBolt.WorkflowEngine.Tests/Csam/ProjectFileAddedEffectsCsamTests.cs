using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Moq;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// The post-add path is the one place that can reach the summarizer, the indexer and the media
/// derivative request. A quarantined file must stop there: none of those may ever see it.
/// </summary>
public class ProjectFileAddedEffectsCsamTests
{
    [Fact]
    public async Task ProjectFileAddedEffects_QuarantinedFile_SkipsIndexingAndDerivatives()
    {
        Mock<IPublishEndpoint> publish = new();
        Mock<IBackgroundTaskQueue<FileSummarizationTask>> queue = new();
        Mock<IFileStorageService> storage = new();
        Mock<IUserActivityNotifier> activity = new();
        ProjectFileAddedEffects effects = new(
            publish.Object, queue.Object, storage.Object, activity.Object,
            logger: null, csamMatcher: new QuarantiningMatcher());

        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            OriginalFileName = "clip.mp4",
            MimeType = "video/mp4",
            SummaryStatus = SummaryStatus.Pending,
            CsamStatus = CsamScanStatus.Pending
        };

        await effects.RunAsync(file, Guid.NewGuid(), CancellationToken.None);

        file.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        publish.Verify(
            p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()),
            Times.Never, "a quarantined file is never indexed");
        queue.Verify(q => q.TryQueue(It.IsAny<FileSummarizationTask>()), Times.Never);
        activity.Verify(
            a => a.FileCreatedAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "a quarantined file is never announced to the activity feed");
    }

    [Fact]
    public async Task ProjectFileAddedEffects_AlreadyQuarantinedRow_ReturnsBeforeAnyEffect()
    {
        Mock<IPublishEndpoint> publish = new();
        Mock<IBackgroundTaskQueue<FileSummarizationTask>> queue = new();
        Mock<IFileStorageService> storage = new();
        ProjectFileAddedEffects effects = new(
            publish.Object, queue.Object, storage.Object, activityNotifier: null, logger: null, csamMatcher: new QuarantiningMatcher());

        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            OriginalFileName = "notes.txt",
            MimeType = "text/plain",
            SummaryStatus = SummaryStatus.Pending,
            CsamStatus = CsamScanStatus.Quarantined
        };

        await effects.RunAsync(file, Guid.NewGuid(), CancellationToken.None);

        publish.Verify(
            p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()),
            Times.Never);
        queue.Verify(q => q.TryQueue(It.IsAny<FileSummarizationTask>()), Times.Never);
    }

    /// <summary>Returns a match and marks the row, exactly as the real matcher does.</summary>
    private sealed class QuarantiningMatcher : ReelBolt.Inference.Api.Services.Csam.ICsmaHashMatcher
    {
        public Task<ReelBolt.Inference.Api.Services.Csam.CsamScanOutcome> ScanAsync(
            ProjectFile file, Guid? actorUserId, CancellationToken ct)
        {
            file.CsamStatus = CsamScanStatus.Quarantined;
            return Task.FromResult(new ReelBolt.Inference.Api.Services.Csam.CsamScanOutcome(
                CsamScanStatus.Quarantined, "csam", "exact", true, null));
        }
    }
}
