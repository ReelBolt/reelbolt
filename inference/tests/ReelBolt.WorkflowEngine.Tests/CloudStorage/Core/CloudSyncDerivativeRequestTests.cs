#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;

/// <summary>
/// P2-LAZY-04 / d2 N6: a video or audio file a cloud sync brings into the project is footage like an
/// upload, so it asks for its derivative set (and stored probe) with <c>requestedBy:"upload"</c> once
/// its row is committed, instead of waiting for someone to open it in the editor.
/// </summary>
public class CloudSyncDerivativeRequestTests
{
    private static async Task<(CloudFixture F, Project Project, ProjectCloudLink Link)> NewAsync()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection connection = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, connection, null);
        return (f, project, link);
    }

    [Fact]
    public async Task A_synced_video_requests_its_derivative_set_and_a_text_file_does_not()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("clips/take1.mp4", "not-really-video", "r1");
        f.Provider.AddFile("notes.md", "hello", "r1");

        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        ProjectFile video = (await f.SyncedFilesAsync(project.Id)).Single(r => r.OriginalPath == "clips/take1.mp4");
        MediaDerivativesRequested request = f.Published.Should().ContainSingle().Subject;
        request.ProjectId.Should().Be(project.Id);
        request.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.ProjectFile, video.Id));
        request.SourceStorageKey.Should().Be(video.StorageKey);
        request.MimeType.Should().Be("video/mp4");
        request.RequestedBy.Should().Be("upload");
    }

    [Fact]
    public async Task An_unchanged_video_is_not_requested_again()
    {
        (CloudFixture f, _, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("take1.mp4", "bytes", "r1");

        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        f.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task A_broker_failure_never_fails_the_sync()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Publisher
            .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker down"));
        f.Provider.AddFile("take1.mp4", "bytes", "r1");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Status.Should().Be("synced");
        (await f.SyncedFilesAsync(project.Id)).Should().ContainSingle();
    }

    // docs/csam.md: no derivative before the CSAM verdict.

    [Fact]
    public async Task A_synced_video_is_scanned_before_its_derivative_request()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("take1.mp4", "bytes", "r1");
        bool scannedBeforePublish = false;
        f.Publisher
            .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativesRequested, CancellationToken>((m, _) =>
            {
                scannedBeforePublish = f.Csam.Scanned.Contains(m.SourceRef.SourceId);
                f.Published.Add(m);
            })
            .Returns(Task.CompletedTask);

        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        f.Published.Should().ContainSingle();
        scannedBeforePublish.Should().BeTrue();
    }

    [Fact]
    public async Task A_synced_video_that_matches_is_quarantined_and_requests_nothing()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Csam.Status = CsamScanStatus.Quarantined;
        f.Provider.AddFile("take1.mp4", "bytes", "r1");

        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        f.Csam.Scanned.Should().ContainSingle();
        f.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_synced_video_whose_scan_cannot_settle_waits_for_the_sweeper()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Csam.Status = CsamScanStatus.Pending;
        f.Provider.AddFile("take1.mp4", "bytes", "r1");

        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        f.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_synced_video_the_control_could_not_answer_for_still_requests_its_set()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Csam.Status = CsamScanStatus.NotScanned;
        f.Provider.AddFile("take1.mp4", "bytes", "r1");

        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        f.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task A_synced_image_is_scanned_but_requests_no_derivative_set_and_a_text_file_is_not_scanned()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("pic.png", "bytes", "r1");
        f.Provider.AddFile("notes.md", "hello", "r1");

        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        ProjectFile image = (await f.SyncedFilesAsync(project.Id)).Single(r => r.OriginalPath == "pic.png");
        f.Csam.Scanned.Should().ContainSingle().Which.Should().Be(image.Id);
        f.Published.Should().BeEmpty();
    }
}
