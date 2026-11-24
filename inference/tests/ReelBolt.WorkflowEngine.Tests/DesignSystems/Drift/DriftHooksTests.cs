using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.FolderSync;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>
/// P3-02: the sync-path hooks only enqueue. A full or broken queue must never break a GitHub pull,
/// a cloud sync or a folder upload, and a completed folder upload does ask for a check.
/// </summary>
public sealed class DriftHooksTests
{
    private sealed class ThrowingQueue : DesignDriftQueue
    {
        public override DesignDriftEnqueueResult Enqueue(
            Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths) =>
            throw new InvalidOperationException("the queue is broken");

        public override DesignDriftEnqueueResult Enqueue(
            Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths, Guid organizationId) =>
            throw new InvalidOperationException("the queue is broken");
    }

    private sealed class FixedEntitlements : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(
            Guid organizationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OrganizationEntitlementSet());
    }

    [Fact]
    public void Notify_enqueues_the_project()
    {
        DesignDriftQueue queue = new(new DesignDriftQueueTests.FakeClock());

        DriftHooks.Notify(queue, Guid.NewGuid(), DesignDriftTrigger.CloudSync, ["a.css"]);

        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
        request.ChangedPaths.Should().BeEquivalentTo(["a.css"]);
    }

    [Fact]
    public void Notify_never_throws_when_the_queue_throws()
    {
        Action act = () => DriftHooks.Notify(
            new ThrowingQueue(), Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Notify_never_throws_when_the_queue_is_full()
    {
        DesignDriftQueue queue = new(new DesignDriftQueueTests.FakeClock());
        Guid org = Guid.NewGuid();
        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, org);

        Action act = () => DriftHooks.Notify(queue, Guid.NewGuid(), DesignDriftTrigger.FolderSync, null, organizationId: org);

        act.Should().NotThrow();
    }

    [Fact]
    public void Notify_never_throws_when_the_queue_throws_for_a_named_organization()
    {
        Action act = () => DriftHooks.Notify(
            new ThrowingQueue(), Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, organizationId: Guid.NewGuid());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(DesignDriftTrigger.GitHubPush)]
    [InlineData(DesignDriftTrigger.CloudSync)]
    [InlineData(DesignDriftTrigger.FolderSync)]
    public void One_organization_flooding_the_hooks_stops_at_its_cap_and_leaves_room_for_another(DesignDriftTrigger trigger)
    {
        DesignDriftQueue queue = new(new DesignDriftQueueTests.FakeClock());
        Guid flooder = Guid.NewGuid();
        List<Guid> projects = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList();

        foreach (Guid project in projects)
            DriftHooks.Notify(queue, project, trigger, null, organizationId: flooder);

        projects.Count(queue.IsQueued).Should().Be(DesignDriftQueue.PerOrganizationLimit);
        projects.Take(DesignDriftQueue.PerOrganizationLimit).Should().OnlyContain(p => queue.IsQueued(p));

        Guid other = Guid.NewGuid();
        DriftHooks.Notify(queue, other, trigger, null, organizationId: Guid.NewGuid());
        queue.IsQueued(other).Should().BeTrue("a second organization is still accepted while the first is at its cap");
    }

    [Fact]
    public void Manual_bypasses_the_window_but_not_the_organization_cap()
    {
        DesignDriftQueue queue = new(new DesignDriftQueueTests.FakeClock());
        Guid flooder = Guid.NewGuid();
        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, flooder);

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.Manual, null, flooder)
            .Should().Be(DesignDriftEnqueueResult.OrganizationBusy);
        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.Manual, null, Guid.NewGuid())
            .Should().Be(DesignDriftEnqueueResult.Queued);
    }

    [Fact]
    public void Manual_for_an_unknown_organization_obeys_the_shared_lane_cap()
    {
        DesignDriftQueue queue = new(new DesignDriftQueueTests.FakeClock());
        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null);

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.Manual, null)
            .Should().Be(DesignDriftEnqueueResult.OrganizationBusy);
    }

    [Fact]
    public void Notify_ignores_a_missing_queue()
    {
        Action act = () => DriftHooks.Notify(null, Guid.NewGuid(), DesignDriftTrigger.Sweep, null);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task A_completed_folder_upload_fires_the_hook()
    {
        await using InferenceApiDbContext db = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"drift-hook-folder-{Guid.NewGuid():N}")
                .Options);

        Guid ownerId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = ownerId,
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ProjectLocalFolderLink link = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = organizationId,
            DeviceId = Guid.NewGuid(),
            TargetFolder = "Footage",
            DisplayPath = "D:\\Footage",
            CreatedByUserId = ownerId,
        };
        ProjectFile written = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "clip.mp4",
            OriginalPath = "Footage/clip.mp4",
            StorageKey = $"projects/{project.Id}/userFiles/clip.mp4",
            MimeType = "video/mp4",
            SizeBytes = 10,
            SourceKind = LocalFolderSyncService.SourceKind,
            SourceLinkId = link.Id,
            UploadedAt = DateTime.UtcNow,
        };
        db.Projects.Add(project);
        db.ProjectLocalFolderLinks.Add(link);
        db.ProjectFiles.Add(written);
        await db.SaveChangesAsync();

        DesignDriftQueue queue = new(new DesignDriftQueueTests.FakeClock());
        LocalFolderSyncService service = new(
            db,
            Mock.Of<IFileStorageService>(),
            new FixedEntitlements(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalFolderSyncService>.Instance,
            TimeProvider.System,
            queue);

        await service.RecordUploadAsync(link, written, CancellationToken.None);

        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
        request.ProjectId.Should().Be(project.Id);
        request.Trigger.Should().Be(DesignDriftTrigger.FolderSync);
        request.ChangedPaths.Should().BeEquivalentTo(["Footage/clip.mp4"]);
    }
}
