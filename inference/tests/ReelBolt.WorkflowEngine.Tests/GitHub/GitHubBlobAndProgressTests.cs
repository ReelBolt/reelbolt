#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// The production failure of 2026-10 (docs/github-app.md "Empty and unreadable files"): a repository
/// holding one zero-byte file (<c>web/public/.gitkeep</c>) could never be synced, because GitHub sends
/// such a blob as <c>{"encoding":"base64","content":""}</c> and the client called that a 502 — which the
/// link then reported as "GitHub had a problem answering". Plus the two properties that make a long pull
/// survivable: one unreadable file is skipped rather than fatal, and progress is saved as it goes.
/// </summary>
public class GitHubBlobAndProgressTests
{
    // ------------------------------------------------------------ the real client, over a captured transport

    private static (GitHubAppClient Client, CapturingHttpHandler Handler) RealClient(Func<HttpRequestMessage, HttpResponseMessage?> blobs)
    {
        CapturingHttpHandler handler = new();
        handler.Responder = (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/access_tokens", StringComparison.Ordinal))
                return handler.Json("{\"token\":\"ghs_installation_token\",\"expires_at\":\"2099-01-01T00:00:00Z\"}");
            return blobs(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        using RSA rsa = RSA.Create(2048);
        GitHubOptions options = new()
        {
            AppId = "123456",
            PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
            WebhookSecret = "s",
            ApiBaseUrl = "https://api.github.com"
        };

        return (new GitHubAppClient(new HttpClient(handler), Options.Create(options), NullLogger<GitHubAppClient>.Instance), handler);
    }

    [Theory]
    [InlineData("{\"sha\":\"e69de29\",\"size\":0,\"encoding\":\"base64\",\"content\":\"\"}")]
    [InlineData("{\"sha\":\"e69de29\",\"size\":0,\"encoding\":\"base64\"}")]
    public async Task A_zero_byte_blob_is_an_empty_file_not_a_GitHub_failure(string json)
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = RealClient(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/git/blobs/e69de29", StringComparison.Ordinal) ? new CapturingHttpHandler().Json(json) : null);

        byte[] bytes = await client.GetBlobAsync(GitHubTestData.InstallationId, "acme", "promo-video", "e69de29", CancellationToken.None);

        bytes.Should().BeEmpty();
        handler.Requests.Should().Contain(r => r.Url.EndsWith("/git/blobs/e69de29", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_blob_wrapped_by_GitHub_at_60_characters_decodes()
    {
        string base64 = Convert.ToBase64String(new byte[100]);
        string wrapped = string.Join("\\n", Enumerable.Range(0, (base64.Length + 59) / 60).Select(i => base64.Substring(i * 60, Math.Min(60, base64.Length - (i * 60)))));
        (GitHubAppClient client, _) = RealClient(_ => new CapturingHttpHandler().Json($"{{\"encoding\":\"base64\",\"content\":\"{wrapped}\"}}"));

        byte[] bytes = await client.GetBlobAsync(GitHubTestData.InstallationId, "acme", "promo-video", "abc", CancellationToken.None);

        bytes.Should().HaveCount(100);
    }

    [Theory]
    [InlineData(null, GitHubBlobUnreadableException.NotFound)]
    [InlineData("{\"encoding\":\"utf-8\",\"content\":\"hello\"}", GitHubBlobUnreadableException.UnsupportedEncoding)]
    [InlineData("{\"encoding\":\"base64\",\"content\":\"not*base64!\"}", GitHubBlobUnreadableException.Malformed)]
    public async Task A_blob_GitHub_does_not_have_or_does_not_send_as_base64_is_unreadable_not_a_GitHub_outage(string? json, string reason)
    {
        (GitHubAppClient client, _) = RealClient(_ => json is null ? null : new CapturingHttpHandler().Json(json));

        Func<Task> act = () => client.GetBlobAsync(GitHubTestData.InstallationId, "acme", "promo-video", "abc", CancellationToken.None);

        GitHubBlobUnreadableException unreadable = (await act.Should().ThrowAsync<GitHubBlobUnreadableException>()).Which;
        unreadable.Reason.Should().Be(reason);
        unreadable.BlobSha.Should().Be("abc");
        unreadable.Should().NotBeAssignableTo<GitHubApiException>("nothing may reword it as a GitHub failure");
    }

    [Fact]
    public async Task A_real_GitHub_server_error_on_a_blob_is_still_a_GitHub_failure()
    {
        (GitHubAppClient client, _) = RealClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        Func<Task> act = () => client.GetBlobAsync(GitHubTestData.InstallationId, "acme", "promo-video", "abc", CancellationToken.None);

        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // ------------------------------------------------------------ a pull

    private sealed class Fixture
    {
        public required TestTenant Tenant { get; init; }
        public required string DatabaseName { get; init; }
        public required InMemoryDatabaseRoot Root { get; init; }
        public required InferenceApiDbContext Db { get; init; }
        public required FakeGitHubAppClient GitHub { get; init; }
        public required InMemoryFileStorage Storage { get; init; }
        public required ProjectGitHubLink Link { get; init; }
        public required Guid ProjectId { get; init; }

        /// <summary>A context of its own over the same store: what was SAVED, not what one context tracks.</summary>
        public InferenceApiDbContext FreshDb() => GitHubTestDb.New(Tenant, DatabaseName, Root);

        public GitHubRepositoryPullService ServiceOver(InferenceApiDbContext db) =>
            new(db, GitHub, Storage, Options.Create(GitHubTestData.Configured()), NullLogger<GitHubRepositoryPullService>.Instance);
    }

    private static async Task<Fixture> NewAsync()
    {
        TestTenant tenant = new();
        string name = $"github-progress-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant, name, root);

        Guid ownerId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(ownerId);
        Project project = GitHubTestData.NewProject(organizationId, ownerId);
        db.Projects.Add(project);
        ProjectGitHubLink link = GitHubTestData.NewLink(project.Id, organizationId, ownerId);
        db.ProjectGitHubLinks.Add(link);
        await db.SaveChangesAsync();

        FakeGitHubAppClient gitHub = new();
        gitHub.AddRepository(GitHubTestData.RepositoryFullName, GitHubTestData.InstallationId);

        return new Fixture
        {
            Tenant = tenant,
            DatabaseName = name,
            Root = root,
            Db = db,
            GitHub = gitHub,
            Storage = new InMemoryFileStorage(),
            Link = link,
            ProjectId = project.Id
        };
    }

    private static List<ProjectFile> Saved(Fixture fixture)
    {
        using InferenceApiDbContext db = fixture.FreshDb();
        return db.ProjectFiles
            .Where(f => f.ProjectId == fixture.ProjectId && f.SourceKind == "github")
            .AsEnumerable()
            .OrderBy(f => f.OriginalPath, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public async Task A_pull_writes_a_zero_byte_file_like_any_other()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", new Dictionary<string, string>
        {
            ["web/public/.gitkeep"] = "",
            ["web/app/page.tsx"] = "export default 1;",
        });

        GitHubPullOutcome outcome = await fixture.ServiceOver(fixture.Db).PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        outcome.Status.Should().Be("pulled");
        outcome.Written.Should().Be(2);
        outcome.Skipped.Should().Be(0);
        ProjectFile keep = Saved(fixture).Single(f => f.OriginalPath == "web/public/.gitkeep");
        keep.SizeBytes.Should().Be(0);
        fixture.Storage.Objects[keep.StorageKey].Should().BeEmpty();
        fixture.Link.LastError.Should().BeNull();
        fixture.Link.LastPulledCommit.Should().Be("c1");
    }

    [Fact]
    public async Task A_quarantined_file_is_never_overwritten_or_deleted_by_a_pull()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", new Dictionary<string, string>
        {
            ["bad.jpg"] = "old",
            ["gone.jpg"] = "old",
        });
        await fixture.ServiceOver(fixture.Db).PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        string overwriteKey;
        string vanishKey;
        using (InferenceApiDbContext db = fixture.FreshDb())
        {
            List<ProjectFile> rows = db.ProjectFiles.Where(f => f.ProjectId == fixture.ProjectId).ToList();
            ProjectFile overwrite = rows.Single(f => f.OriginalPath == "bad.jpg");
            ProjectFile vanish = rows.Single(f => f.OriginalPath == "gone.jpg");
            overwriteKey = $"projects/{fixture.ProjectId}/quarantine/{overwrite.Id}/bad.jpg";
            vanishKey = $"projects/{fixture.ProjectId}/quarantine/{vanish.Id}/gone.jpg";
            overwrite.StorageKey = overwriteKey;
            overwrite.CsamQuarantineKey = overwriteKey;
            overwrite.CsamStatus = CsamScanStatus.Quarantined;
            vanish.StorageKey = vanishKey;
            vanish.CsamQuarantineKey = vanishKey;
            vanish.CsamStatus = CsamScanStatus.Quarantined;
            await db.SaveChangesAsync();
        }
        fixture.Storage.Objects[overwriteKey] = [1, 2, 3];
        fixture.Storage.Objects[vanishKey] = [4, 5, 6];

        // The repository changes bad.jpg and removes gone.jpg. Neither may touch the quarantine.
        fixture.GitHub.AddTree("c2", new Dictionary<string, string> { ["bad.jpg"] = "new" });
        using (InferenceApiDbContext db = fixture.FreshDb())
        {
            ProjectGitHubLink link = db.ProjectGitHubLinks.Single(l => l.ProjectId == fixture.ProjectId);
            GitHubPullOutcome outcome = await fixture.ServiceOver(db).PullAsync(
                link, "c2", DateTime.UtcNow.AddMinutes(1), "d2", CancellationToken.None);

            outcome.Skipped.Should().Be(2, "both the overwrite and the vanished path are kept as evidence");
            outcome.Deleted.Should().Be(0);
        }

        fixture.Storage.Deletes.Should().Be(0);
        fixture.Storage.Objects.Should().ContainKey(overwriteKey);
        fixture.Storage.Objects.Should().ContainKey(vanishKey);
        Saved(fixture).Should().HaveCount(2).And.OnlyContain(f => f.CsamStatus == CsamScanStatus.Quarantined);
    }

    [Fact]
    public async Task A_blob_GitHub_cannot_return_is_skipped_and_counted_and_the_rest_of_the_pull_completes()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", new Dictionary<string, string>
        {
            ["a.txt"] = "one",
            ["gone.bin"] = "vanished",
            ["b.txt"] = "two",
        });
        // GitHub lists the blob in the tree, then answers 404 (or something not base64) for it.
        fixture.GitHub.Blobs.Remove(FakeGitHubAppClient.Sha("gone.bin", "vanished"));

        GitHubPullOutcome outcome = await fixture.ServiceOver(fixture.Db).PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        outcome.Status.Should().Be("pulled", "one unreadable file must not block every other file");
        outcome.Written.Should().Be(2);
        outcome.Skipped.Should().Be(1);
        Saved(fixture).Select(f => f.OriginalPath).Should().Equal("a.txt", "b.txt");
        fixture.Link.LastError.Should().BeNull();
        fixture.Link.LastPulledCommit.Should().Be("c1");
    }

    [Fact]
    public async Task An_unreadable_blob_does_not_delete_the_copy_a_previous_pull_wrote()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", new Dictionary<string, string> { ["logo.svg"] = "<svg/>" });
        await fixture.ServiceOver(fixture.Db).PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        fixture.GitHub.AddTree("c2", new Dictionary<string, string> { ["logo.svg"] = "<svg new/>" });
        fixture.GitHub.Blobs.Remove(FakeGitHubAppClient.Sha("logo.svg", "<svg new/>"));
        GitHubPullOutcome outcome = await fixture.ServiceOver(fixture.Db).PullAsync(fixture.Link, "c2", DateTime.UtcNow.AddMinutes(1), "d2", CancellationToken.None);

        outcome.Skipped.Should().Be(1);
        outcome.Deleted.Should().Be(0, "the file is still in the repository; only its new content could not be read");
        Saved(fixture).Single().SourceRevision.Should().Be(FakeGitHubAppClient.Sha("logo.svg", "<svg/>"), "the old revision stays, so a later pull asks again");
    }

    [Fact]
    public async Task An_interrupted_pull_keeps_every_saved_batch_and_the_next_pull_resumes_by_blob_sha()
    {
        Fixture fixture = await NewAsync();
        Dictionary<string, string> files = Enumerable.Range(1, 60).ToDictionary(i => $"web/file{i:D2}.txt", i => $"content {i}");
        fixture.GitHub.AddTree("c1", files);

        // The process goes away part-way through: the 30th download is the last one.
        using CancellationTokenSource stop = new();
        fixture.GitHub.OnBlobRequested = _ =>
        {
            if (fixture.GitHub.BlobRequests.Count == 30) stop.Cancel();
        };

        InferenceApiDbContext interruptedDb = fixture.FreshDb();
        ProjectGitHubLink interruptedLink = interruptedDb.ProjectGitHubLinks.Single(l => l.ProjectId == fixture.ProjectId);
        Func<Task> act = () => fixture.ServiceOver(interruptedDb).PullAsync(interruptedLink, "c1", DateTime.UtcNow, "d1", stop.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        // Deliberately no SaveChanges after the interruption: only what the pull saved by itself counts.

        List<ProjectFile> kept = Saved(fixture);
        kept.Should().HaveCount(GitHubRepositoryPullService.SaveEvery, "every completed batch of written files is already saved");
        kept.Should().OnlyContain(f => f.SourceRevision != null && fixture.Storage.Objects.ContainsKey(f.StorageKey),
            "each saved row points at its stored object, so nothing uploaded is orphaned");

        using (InferenceApiDbContext check = fixture.FreshDb())
        {
            ProjectGitHubLink stored = check.ProjectGitHubLinks.Single(l => l.ProjectId == fixture.ProjectId);
            stored.LastPulledCommit.Should().BeNull("the commit is stamped only after a complete pass");
            stored.LastPulledAt.Should().BeNull();
        }

        // The next pull: a fresh context, as the next sync would have.
        fixture.GitHub.OnBlobRequested = null;
        int before = fixture.GitHub.BlobRequests.Count;
        InferenceApiDbContext resumeDb = fixture.FreshDb();
        ProjectGitHubLink resumeLink = resumeDb.ProjectGitHubLinks.Single(l => l.ProjectId == fixture.ProjectId);
        GitHubPullOutcome resumed = await fixture.ServiceOver(resumeDb).PullAsync(resumeLink, "c1", DateTime.UtcNow, "d2", CancellationToken.None);

        resumed.Unchanged.Should().Be(GitHubRepositoryPullService.SaveEvery, "what was saved is not downloaded again");
        resumed.Written.Should().Be(60 - GitHubRepositoryPullService.SaveEvery);
        (fixture.GitHub.BlobRequests.Count - before).Should().Be(60 - GitHubRepositoryPullService.SaveEvery);
        Saved(fixture).Should().HaveCount(60);
        Saved(fixture).Select(f => f.OriginalPath).Should().OnlyHaveUniqueItems("a resumed pull never duplicates a row");
        resumeLink.LastPulledCommit.Should().Be("c1");
    }

    [Fact]
    public async Task Files_removed_from_the_repository_are_deleted_only_after_a_complete_pass()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", new Dictionary<string, string> { ["old.txt"] = "old" });
        await fixture.ServiceOver(fixture.Db).PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        Dictionary<string, string> next = Enumerable.Range(1, 30).ToDictionary(i => $"new{i:D2}.txt", i => $"n{i}");
        fixture.GitHub.AddTree("c2", next);
        using CancellationTokenSource stop = new();
        fixture.GitHub.OnBlobRequested = _ =>
        {
            if (fixture.GitHub.BlobRequests.Count == 1 + 27) stop.Cancel();
        };

        InferenceApiDbContext db = fixture.FreshDb();
        ProjectGitHubLink link = db.ProjectGitHubLinks.Single(l => l.ProjectId == fixture.ProjectId);
        Func<Task> act = () => fixture.ServiceOver(db).PullAsync(link, "c2", DateTime.UtcNow.AddMinutes(1), "d2", stop.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        Saved(fixture).Select(f => f.OriginalPath).Should().Contain("old.txt",
            "an incomplete pass cannot know what vanished, so it deletes nothing");
    }
}
