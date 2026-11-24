#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.FolderSync;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;
using ReelBolt.WorkflowEngine.Tests.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>
/// QA-4: the drift hook at each sync call site. Each test drives the real service and reads what landed
/// on the queue, so removing a <c>DriftHooks.Notify</c> call (or firing it when nothing changed) fails.
/// </summary>
public sealed class DriftHookCallSiteTests
{
    /// <summary>Records the organization each hook charged, then behaves as the real queue.</summary>
    private sealed class OrgRecordingQueue(TimeProvider clock) : DesignDriftQueue(clock)
    {
        public List<Guid> Organizations { get; } = [];

        public override DesignDriftEnqueueResult Enqueue(
            Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths, Guid organizationId)
        {
            Organizations.Add(organizationId);
            return base.Enqueue(projectId, trigger, changedPaths, organizationId);
        }
    }

    [Fact]
    public async Task The_github_hook_charges_the_links_organization()
    {
        GitHubFixture f = await NewGitHubAsync();
        OrgRecordingQueue queue = new(new DesignDriftQueueTests.FakeClock());
        GitHubRepositoryPullService service = new(
            f.Db, f.GitHub, new InMemoryFileStorage(), Options.Create(GitHubTestData.Configured()),
            NullLogger<GitHubRepositoryPullService>.Instance, planGate: null, driftQueue: queue);
        f.GitHub.AddTree("c1", Tree(("a.css", "a")));

        await service.PullAsync(f.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        queue.Organizations.Should().Equal(f.Link.OrganizationId);
    }

    [Fact]
    public async Task The_cloud_hook_charges_the_links_organization()
    {
        (CloudFixture f, ProjectCloudLink link, _, _, _) = await NewCloudAsync();
        OrgRecordingQueue queue = new(new DesignDriftQueueTests.FakeClock());
        CloudSyncService sync = new(
            f.Db, f.Registry, f.Connections, f.Storage, Options.Create(f.Options),
            NullLogger<CloudSyncService>.Instance, f.Clock, planGate: null, driftQueue: queue);
        f.Provider.AddFile("a.css", "a", "r1");

        await sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        queue.Organizations.Should().Equal(link.OrganizationId);
    }

    [Fact]
    public async Task The_folder_delete_hook_charges_the_links_organization()
    {
        (_, ProjectLocalFolderLink link, DesignDriftQueue unused) = await NewFolderAsync("Footage/a.css");
        unused.Dispose();
        // Rebuilt with a recording queue over the same link.
        OrgRecordingQueue queue = new(new DesignDriftQueueTests.FakeClock());
        (LocalFolderSyncService service, ProjectLocalFolderLink own, _) = await NewFolderAsync(queue, "Footage/a.css");

        await service.DeleteFilesAsync(own, ["a.css"], CancellationToken.None);

        queue.Organizations.Should().Equal(own.OrganizationId);
        link.Should().NotBeNull();
    }

    private static void Settle(DesignDriftQueue queue, DesignDriftQueueTests.FakeClock clock, Guid projectId)
    {
        while (queue.Reader.TryRead(out _))
        {
        }

        queue.TryBegin(projectId, out _);
        queue.Complete(projectId);
        clock.Advance(TimeSpan.FromMinutes(2));
    }

    // -------------------------------------------------------------------------------------------------
    // GitHub pull
    // -------------------------------------------------------------------------------------------------

    private sealed class GitHubFixture
    {
        public required InferenceApiDbContext Db { get; init; }
        public required FakeGitHubAppClient GitHub { get; init; }
        public required GitHubRepositoryPullService Service { get; init; }
        public required ProjectGitHubLink Link { get; init; }
        public required DesignDriftQueue Queue { get; init; }
        public required DesignDriftQueueTests.FakeClock Clock { get; init; }
    }

    private static async Task<GitHubFixture> NewGitHubAsync()
    {
        InferenceApiDbContext db = GitHubTestDb.New(new TestTenant());
        Guid ownerId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(ownerId);
        Project project = GitHubTestData.NewProject(organizationId, ownerId);
        db.Projects.Add(project);
        ProjectGitHubLink link = GitHubTestData.NewLink(project.Id, organizationId, ownerId);
        db.ProjectGitHubLinks.Add(link);
        await db.SaveChangesAsync();

        FakeGitHubAppClient gitHub = new();
        gitHub.AddRepository(GitHubTestData.RepositoryFullName, GitHubTestData.InstallationId);
        DesignDriftQueueTests.FakeClock clock = new();
        DesignDriftQueue queue = new(clock);

        return new GitHubFixture
        {
            Db = db,
            GitHub = gitHub,
            Link = link,
            Queue = queue,
            Clock = clock,
            Service = new GitHubRepositoryPullService(
                db, gitHub, new InMemoryFileStorage(), Options.Create(GitHubTestData.Configured()),
                NullLogger<GitHubRepositoryPullService>.Instance, planGate: null, driftQueue: queue),
        };
    }

    private static IReadOnlyDictionary<string, string> Tree(params (string Path, string Content)[] files) =>
        files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);

    [Fact]
    public async Task A_github_pull_that_writes_asks_for_a_push_check_with_the_written_paths()
    {
        GitHubFixture f = await NewGitHubAsync();
        f.GitHub.AddTree("c1", Tree(("app/globals.css", ":root{}"), ("README.md", "# hi")));

        await f.Service.PullAsync(f.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        f.Queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue("the pull committed an upsert");
        request.ProjectId.Should().Be(f.Link.ProjectId);
        request.Trigger.Should().Be(DesignDriftTrigger.GitHubPush);
        request.ChangedPaths.Should().BeEquivalentTo(["app/globals.css", "README.md"]);
    }

    [Fact]
    public async Task A_github_pull_that_only_deletes_asks_for_a_push_check_with_the_removed_path()
    {
        GitHubFixture f = await NewGitHubAsync();
        f.GitHub.AddTree("c1", Tree(("a.css", "a"), ("gone.css", "g")));
        await f.Service.PullAsync(f.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);
        Settle(f.Queue, f.Clock, f.Link.ProjectId);

        f.GitHub.AddTree("c2", Tree(("a.css", "a")));
        await f.Service.PullAsync(f.Link, "c2", DateTime.UtcNow.AddMinutes(1), "d2", CancellationToken.None);

        f.Queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
        request.ChangedPaths.Should().BeEquivalentTo(["gone.css"]);
    }

    [Fact]
    public async Task A_github_pull_that_changes_nothing_asks_for_no_check()
    {
        GitHubFixture f = await NewGitHubAsync();
        f.GitHub.AddTree("c1", Tree(("a.css", "a")));
        await f.Service.PullAsync(f.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);
        Settle(f.Queue, f.Clock, f.Link.ProjectId);

        f.GitHub.AddTree("c2", Tree(("a.css", "a")));
        await f.Service.PullAsync(f.Link, "c2", DateTime.UtcNow.AddMinutes(1), "d2", CancellationToken.None);

        f.Queue.Reader.TryRead(out _).Should().BeFalse();
        f.Queue.IsQueued(f.Link.ProjectId).Should().BeFalse();
    }

    // -------------------------------------------------------------------------------------------------
    // Cloud sync
    // -------------------------------------------------------------------------------------------------

    private static async Task<(CloudFixture F, ProjectCloudLink Link, CloudSyncService Sync, DesignDriftQueue Queue, DesignDriftQueueTests.FakeClock Clock)> NewCloudAsync()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection connection = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, connection);
        DesignDriftQueueTests.FakeClock clock = new();
        DesignDriftQueue queue = new(clock);
        CloudSyncService sync = new(
            f.Db, f.Registry, f.Connections, f.Storage, Options.Create(f.Options),
            NullLogger<CloudSyncService>.Instance, f.Clock, planGate: null, driftQueue: queue);
        return (f, link, sync, queue, clock);
    }

    [Fact]
    public async Task A_cloud_sync_that_writes_asks_for_a_cloud_check_with_the_written_paths()
    {
        (CloudFixture f, ProjectCloudLink link, CloudSyncService sync, DesignDriftQueue queue, _) = await NewCloudAsync();
        f.Provider.AddFile("brand/DESIGN.md", "# brand", "r1");

        await sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
        request.ProjectId.Should().Be(link.ProjectId);
        request.Trigger.Should().Be(DesignDriftTrigger.CloudSync);
        request.ChangedPaths.Should().BeEquivalentTo(["brand/DESIGN.md"]);
    }

    [Fact]
    public async Task A_cloud_sync_that_only_deletes_asks_for_a_cloud_check_with_the_removed_path()
    {
        (CloudFixture f, ProjectCloudLink link, CloudSyncService sync, DesignDriftQueue queue, DesignDriftQueueTests.FakeClock clock) = await NewCloudAsync();
        f.Provider.AddFile("a.css", "a", "r1");
        f.Provider.AddFile("gone.css", "g", "r1");
        await sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        Settle(queue, clock, link.ProjectId);

        f.Provider.RemoveFile("gone.css");
        await sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
        request.ChangedPaths.Should().BeEquivalentTo(["gone.css"]);
    }

    [Fact]
    public async Task A_cloud_sync_that_changes_nothing_asks_for_no_check()
    {
        (CloudFixture f, ProjectCloudLink link, CloudSyncService sync, DesignDriftQueue queue, DesignDriftQueueTests.FakeClock clock) = await NewCloudAsync();
        f.Provider.AddFile("a.css", "a", "r1");
        await sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        Settle(queue, clock, link.ProjectId);

        await sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        queue.Reader.TryRead(out _).Should().BeFalse();
        queue.IsQueued(link.ProjectId).Should().BeFalse();
    }

    // -------------------------------------------------------------------------------------------------
    // Local folder delete
    // -------------------------------------------------------------------------------------------------

    private sealed class FixedEntitlements : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(
            Guid organizationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OrganizationEntitlementSet());
    }

    private static Task<(LocalFolderSyncService Service, ProjectLocalFolderLink Link, DesignDriftQueue Queue)> NewFolderAsync(
        params string[] storedPaths) => NewFolderAsync(null, storedPaths);

    private static async Task<(LocalFolderSyncService Service, ProjectLocalFolderLink Link, DesignDriftQueue Queue)> NewFolderAsync(
        DesignDriftQueue? useQueue, params string[] storedPaths)
    {
        InferenceApiDbContext db = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"drift-hook-folder-delete-{Guid.NewGuid():N}")
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
        db.Projects.Add(project);
        db.ProjectLocalFolderLinks.Add(link);
        foreach (string stored in storedPaths)
        {
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OriginalFileName = stored[(stored.LastIndexOf('/') + 1)..],
                OriginalPath = stored,
                StorageKey = $"projects/{project.Id}/userFiles/{Guid.NewGuid():N}",
                MimeType = "text/plain",
                SizeBytes = 1,
                SourceKind = LocalFolderSyncService.SourceKind,
                SourceLinkId = link.Id,
                UploadedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();

        DesignDriftQueue queue = useQueue ?? new(new DesignDriftQueueTests.FakeClock());
        LocalFolderSyncService service = new(
            db, Mock.Of<IFileStorageService>(), new FixedEntitlements(),
            NullLogger<LocalFolderSyncService>.Instance, TimeProvider.System, queue);
        return (service, link, queue);
    }

    [Fact]
    public async Task A_folder_delete_that_removes_rows_asks_for_a_check_with_the_deleted_paths()
    {
        (LocalFolderSyncService service, ProjectLocalFolderLink link, DesignDriftQueue queue) =
            await NewFolderAsync("Footage/a.css", "Footage/b.css");

        int deleted = await service.DeleteFilesAsync(link, ["a.css"], CancellationToken.None);

        deleted.Should().Be(1);
        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
        request.ProjectId.Should().Be(link.ProjectId);
        request.Trigger.Should().Be(DesignDriftTrigger.FolderSync);
        request.ChangedPaths.Should().BeEquivalentTo(["Footage/a.css"]);
    }

    [Fact]
    public async Task A_folder_delete_that_removes_nothing_asks_for_no_check()
    {
        (LocalFolderSyncService service, ProjectLocalFolderLink link, DesignDriftQueue queue) =
            await NewFolderAsync("Footage/a.css");

        (await service.DeleteFilesAsync(link, ["never-existed.css"], CancellationToken.None)).Should().Be(0);
        (await service.DeleteFilesAsync(link, ["../escape.css"], CancellationToken.None)).Should().Be(0);

        queue.Reader.TryRead(out _).Should().BeFalse("nothing was deleted, so nothing changed");
        queue.IsQueued(link.ProjectId).Should().BeFalse();
    }
}
