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
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;
using ReelBolt.WorkflowEngine.Tests.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The shared sync path, both halves of it: a GitHub pull and a cloud-drive sync run the SAME
/// <see cref="SyncStorageQuota"/> over the SAME live usage, so a file that would exceed the plan's
/// quota is skipped (not thrown), the sync still applies deletions and leaves unchanged files alone,
/// and the link's LastError is the one plain-language sentence.
/// </summary>
public class StorageQuotaSyncPathTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static readonly OrganizationEntitlementSet Trial = new() { Tier = "trial", StorageGb = 1 };

    private static PlanGateService Gate(InferenceApiDbContext db) => new(db, new UploadsEntitlements(Trial));

    [Fact]
    public async Task A_github_pull_skips_what_does_not_fit_but_still_deletes_and_keeps_unchanged_files()
    {
        TestTenant tenant = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant);
        Guid owner = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        Project project = GitHubTestData.NewProject(org, owner);
        db.Projects.Add(project);
        ProjectGitHubLink link = GitHubTestData.NewLink(project.Id, org, owner);
        db.ProjectGitHubLinks.Add(link);

        // One small change away from full: a user file of the workspace's own.
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "keep.bin",
            OriginalPath = "keep.bin",
            StorageKey = "projects/keep",
            SizeBytes = Gb - 50
        });

        const string sameContent = "same";
        string sameSha = FakeGitHubAppClient.Sha("same.txt", sameContent);
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "same.txt",
            OriginalPath = "same.txt",
            StorageKey = "projects/same",
            SizeBytes = 4,
            SourceKind = "github",
            SourceRevision = sameSha
        });

        string goneKey = "projects/gone";
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "gone.txt",
            OriginalPath = "gone.txt",
            StorageKey = goneKey,
            SizeBytes = 0,
            SourceKind = "github",
            SourceRevision = "sha-gone"
        });
        await db.SaveChangesAsync();

        FakeGitHubAppClient gitHub = new();
        gitHub.AddRepository(GitHubTestData.RepositoryFullName, GitHubTestData.InstallationId);
        gitHub.AddTree("c1", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["same.txt"] = sameContent,
            ["new.txt"] = new string('x', 100)
        });

        InMemoryFileStorage storage = new();
        storage.Objects[goneKey] = new byte[1];

        GitHubRepositoryPullService service = new(
            db, gitHub, storage, Microsoft.Extensions.Options.Options.Create(GitHubTestData.Configured()),
            NullLogger<GitHubRepositoryPullService>.Instance, Gate(db));

        GitHubPullOutcome outcome = await service.PullAsync(link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        outcome.Written.Should().Be(0, "the new file did not fit");
        outcome.Skipped.Should().Be(1);
        outcome.Unchanged.Should().Be(1, "an unchanged file is still left alone");
        outcome.Deleted.Should().Be(1, "a deletion still applies when storage is full");

        gitHub.BlobRequests.Should().BeEmpty("a full workspace does not download what it cannot keep");
        storage.Objects.Should().NotContainKey(goneKey);
        (await db.ProjectFiles.Where(f => f.ProjectId == project.Id).Select(f => f.OriginalPath).ToListAsync())
            .Should().BeEquivalentTo("keep.bin", "same.txt");
        db.ProjectFiles.Single(f => f.OriginalPath == "keep.bin").SizeBytes.Should().Be(Gb - 50);

        link.LastError.Should().Be(SyncStorageQuota.StorageFullMessage((Gb - 50) + 4, Gb, 1));
        link.LastError.Should().StartWith("Storage is full (").And.Contain("1 file was not copied");
    }

    [Fact]
    public async Task A_cloud_sync_skips_what_does_not_fit_but_still_deletes_and_keeps_unchanged_files()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection connection = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, connection);

        f.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "keep.bin",
            OriginalPath = "keep.bin",
            StorageKey = "projects/keep",
            SizeBytes = Gb - 50
        });
        f.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "same.txt",
            OriginalPath = "same.txt",
            StorageKey = "projects/same",
            SizeBytes = 4,
            SourceKind = f.SourceKind,
            SourceRevision = "r1"
        });
        string goneKey = "projects/cloud/gone";
        f.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "gone.txt",
            OriginalPath = "gone.txt",
            StorageKey = goneKey,
            SizeBytes = 0,
            SourceKind = f.SourceKind,
            SourceRevision = "r0"
        });
        await f.Db.SaveChangesAsync();

        f.Provider.AddFile("same.txt", "same", "r1");
        f.Provider.AddFile("new.txt", new string('x', 100), "r1");
        f.Storage.Objects[goneKey] = new byte[1];

        CloudSyncService gated = new(
            f.Db, f.Registry, f.Connections, f.Storage, Microsoft.Extensions.Options.Options.Create(f.Options),
            NullLogger<CloudSyncService>.Instance, f.Clock, Gate(f.Db));

        CloudSyncOutcome outcome = await gated.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Written.Should().Be(0);
        outcome.Skipped.Should().Be(1);
        outcome.Unchanged.Should().Be(1);
        outcome.Deleted.Should().Be(1);

        f.Provider.Downloads.Should().BeEmpty("the declared size is refused before the bytes are fetched");
        f.Storage.Objects.Should().NotContainKey(goneKey);
        (await f.SyncedFilesAsync(project.Id)).Select(r => r.OriginalPath).Should().Equal("same.txt");

        link.LastError.Should().Be(SyncStorageQuota.StorageFullMessage((Gb - 50) + 4, Gb, 1));
        link.LastError.Should().StartWith("Storage is full (").And.Contain("1 file was not copied");
    }

    [Fact]
    public async Task A_sync_within_the_quota_reports_no_error()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection connection = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, connection);
        f.Provider.AddFile("small.txt", "small", "r1");

        CloudSyncService gated = new(
            f.Db, f.Registry, f.Connections, f.Storage, Microsoft.Extensions.Options.Options.Create(f.Options),
            NullLogger<CloudSyncService>.Instance, f.Clock, Gate(f.Db));

        CloudSyncOutcome outcome = await gated.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Written.Should().Be(1);
        outcome.Skipped.Should().Be(0);
        link.LastError.Should().BeNull("nothing was left out, so the link is clean");
    }
}
