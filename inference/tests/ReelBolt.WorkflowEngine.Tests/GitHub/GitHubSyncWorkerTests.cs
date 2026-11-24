#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// The background half of "Sync now", a new link and a changed selection (docs/github-app.md "Syncs
/// run in the background"): the worker pulls in its own scope, records the outcome on the link in plain
/// words — never blaming GitHub for a fault that is not GitHub's — and releases the project's sync slot
/// only once that outcome is saved.
/// </summary>
public class GitHubSyncWorkerTests
{
    private sealed class Fixture : IDisposable
    {
        public required TestTenant Tenant { get; init; }
        public required string DatabaseName { get; init; }
        public required InMemoryDatabaseRoot Root { get; init; }
        public required FakeGitHubAppClient GitHub { get; init; }
        public required GitHubSyncHarness Sync { get; init; }
        public required GitHubSyncThrottle Throttle { get; init; }
        public required Guid ProjectId { get; init; }

        public ProjectGitHubLink StoredLink()
        {
            using InferenceApiDbContext db = GitHubTestDb.New(Tenant, DatabaseName, Root);
            return db.ProjectGitHubLinks.AsNoTracking().Single(l => l.ProjectId == ProjectId);
        }

        public int PulledFiles()
        {
            using InferenceApiDbContext db = GitHubTestDb.New(Tenant, DatabaseName, Root);
            return db.ProjectFiles.Count(f => f.ProjectId == ProjectId && f.SourceKind == "github");
        }

        /// <summary>Queues a sync the way the controller does: take the slot, hand it to the queue.</summary>
        public void Enqueue(bool force = false)
        {
            GitHubSyncThrottle.Lease lease = Throttle.TryBegin(ProjectId, enforceCooldown: false, out _)
                ?? throw new InvalidOperationException("the project is already syncing");
            Sync.Queue.Enqueue(ProjectId, force, lease);
        }

        public void Dispose() => Sync.Dispose();
    }

    private static async Task<Fixture> NewAsync(IFileStorageService? storage = null)
    {
        TestTenant tenant = new();
        string name = $"github-worker-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();

        Guid ownerId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(ownerId);
        Project project = GitHubTestData.NewProject(organizationId, ownerId);
        using (InferenceApiDbContext db = GitHubTestDb.New(tenant, name, root))
        {
            db.Projects.Add(project);
            db.ProjectGitHubLinks.Add(GitHubTestData.NewLink(project.Id, organizationId, ownerId));
            await db.SaveChangesAsync();
        }

        FakeGitHubAppClient gitHub = new();
        gitHub.AddRepository(GitHubTestData.RepositoryFullName, GitHubTestData.InstallationId);
        gitHub.BranchHeads["acme/promo-video@main"] = "c1";
        gitHub.AddTree("c1", new Dictionary<string, string>
        {
            ["web/public/.gitkeep"] = "",
            ["web/app/page.tsx"] = "page",
            ["brand/logo.svg"] = "<svg/>",
        });

        return new Fixture
        {
            Tenant = tenant,
            DatabaseName = name,
            Root = root,
            GitHub = gitHub,
            Sync = new GitHubSyncHarness(tenant, () => GitHubTestDb.New(tenant, name, root), gitHub, storage ?? new InMemoryFileStorage(), Options.Create(GitHubTestData.Configured())),
            Throttle = new GitHubSyncThrottle(),
            ProjectId = project.Id
        };
    }

    [Fact]
    public async Task A_queued_sync_holds_the_projects_slot_until_the_worker_has_saved_the_outcome()
    {
        using Fixture fixture = await NewAsync();
        fixture.Enqueue();
        fixture.Throttle.IsRunning(fixture.ProjectId).Should().BeTrue("queued counts as syncing");

        (await fixture.Sync.DrainAsync()).Should().Be(1);

        fixture.Throttle.IsRunning(fixture.ProjectId).Should().BeFalse();
        ProjectGitHubLink link = fixture.StoredLink();
        link.LastPulledCommit.Should().Be("c1");
        link.LastPulledFileCount.Should().Be(3, "the zero-byte .gitkeep is a file like any other");
        link.LastError.Should().BeNull();
        fixture.PulledFiles().Should().Be(3);
    }

    [Fact]
    public async Task A_GitHub_failure_is_recorded_on_the_link_in_plain_words()
    {
        using Fixture fixture = await NewAsync();
        fixture.GitHub.TreeFailure = new GitHubApiException("GitHub answered 502 for /repos/acme/promo-video/git/trees/c1?recursive=1.", HttpStatusCode.BadGateway);
        fixture.Enqueue();

        await fixture.Sync.DrainAsync();

        fixture.StoredLink().LastError.Should().Contain("GitHub had a problem answering").And.NotContain("/repos/");
        fixture.Throttle.IsRunning(fixture.ProjectId).Should().BeFalse();
    }

    [Fact]
    public async Task A_fault_inside_ReelBolt_is_never_worded_as_a_GitHub_problem()
    {
        Mock<IFileStorageService> storage = new();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new IOException("the storage backend is down"));
        using Fixture fixture = await NewAsync(storage.Object);
        fixture.Enqueue();

        await fixture.Sync.DrainAsync();

        string? error = fixture.StoredLink().LastError;
        error.Should().Be(GitHubSyncWorker.InternalFailureMessage);
        error.Should().NotContain("GitHub had a problem").And.NotContain("storage backend", "operator detail stays in the log");
        fixture.Throttle.IsRunning(fixture.ProjectId).Should().BeFalse();
    }

    [Fact]
    public async Task A_sync_whose_link_was_removed_while_it_waited_does_nothing_and_frees_the_slot()
    {
        using Fixture fixture = await NewAsync();
        fixture.Enqueue();
        using (InferenceApiDbContext db = GitHubTestDb.New(fixture.Tenant, fixture.DatabaseName, fixture.Root))
        {
            db.ProjectGitHubLinks.RemoveRange(db.ProjectGitHubLinks.Where(l => l.ProjectId == fixture.ProjectId));
            await db.SaveChangesAsync();
        }

        (await fixture.Sync.DrainAsync()).Should().Be(1);

        fixture.GitHub.TreeRequests.Should().BeEmpty();
        fixture.Throttle.IsRunning(fixture.ProjectId).Should().BeFalse();
    }

    [Fact]
    public async Task The_hosted_worker_picks_up_a_queued_sync_on_its_own()
    {
        using Fixture fixture = await NewAsync();
        await fixture.Sync.Worker.StartAsync(CancellationToken.None);
        try
        {
            fixture.Enqueue();

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (fixture.Throttle.IsRunning(fixture.ProjectId) && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            fixture.Throttle.IsRunning(fixture.ProjectId).Should().BeFalse("the worker finished the sync and released the slot");
            fixture.StoredLink().LastPulledCommit.Should().Be("c1");
        }
        finally
        {
            await fixture.Sync.Worker.StopAsync(CancellationToken.None);
        }
    }
}
