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
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// What "pull" means, and why it is safe to run the same push twice (docs/github-app.md).
///
/// <para>
/// The contract under test: a pull makes the project's GitHub-sourced files equal the repository tree
/// at one commit, touching nothing else; an unchanged file costs no download and no upload; and a
/// redelivery, an out-of-order delivery or a mid-flight failure all converge on the same state rather
/// than accumulating duplicated work.
/// </para>
/// </summary>
public class GitHubRepositoryPullServiceTests
{
    private sealed class Fixture
    {
        public required InferenceApiDbContext Db { get; init; }
        public required FakeGitHubAppClient GitHub { get; init; }
        public required InMemoryFileStorage Storage { get; init; }
        public required GitHubRepositoryPullService Service { get; init; }
        public required ProjectGitHubLink Link { get; init; }
        public required Guid ProjectId { get; init; }
    }

    private static async Task<Fixture> NewAsync(GitHubOptions? options = null, string? branch = null, string? pathPrefix = null)
    {
        TestTenant tenant = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid ownerId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(ownerId);
        Project project = GitHubTestData.NewProject(organizationId, ownerId);
        db.Projects.Add(project);

        ProjectGitHubLink link = GitHubTestData.NewLink(project.Id, organizationId, ownerId);
        link.Branch = branch;
        link.PathPrefix = pathPrefix;
        db.ProjectGitHubLinks.Add(link);
        await db.SaveChangesAsync();

        FakeGitHubAppClient gitHub = new();
        gitHub.AddRepository(GitHubTestData.RepositoryFullName, GitHubTestData.InstallationId);
        InMemoryFileStorage storage = new();

        GitHubRepositoryPullService service = new(
            db,
            gitHub,
            storage,
            Options.Create(options ?? GitHubTestData.Configured()),
            NullLogger<GitHubRepositoryPullService>.Instance);

        return new Fixture { Db = db, GitHub = gitHub, Storage = storage, Service = service, Link = link, ProjectId = project.Id };
    }

    private static IReadOnlyDictionary<string, string> Tree(params (string Path, string Content)[] files) =>
        files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);

    private static List<ProjectFile> Pulled(Fixture fixture) =>
        fixture.Db.ProjectFiles
            .Where(f => f.ProjectId == fixture.ProjectId && f.SourceKind == "github")
            .AsEnumerable()
            .OrderBy(f => f.OriginalPath, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public async Task A_first_pull_materializes_the_tree_and_marks_every_row_it_wrote()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", Tree(("scene.tsx", "export const A = 1;"), ("assets/logo.svg", "<svg/>"), ("README.md", "# hi")));

        GitHubPullOutcome outcome = await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        outcome.Status.Should().Be("pulled");
        outcome.Written.Should().Be(3);
        outcome.Deleted.Should().Be(0);

        List<ProjectFile> rows = Pulled(fixture);
        rows.Select(r => r.OriginalPath).Should().Equal("README.md", "assets/logo.svg", "scene.tsx");
        rows.Should().OnlyContain(r => r.SourceKind == "github");
        rows.Should().OnlyContain(r => r.SourceRevision != null);
        rows.Single(r => r.OriginalPath == "assets/logo.svg").DirectoryPath.Should().Be("assets");
        rows.Single(r => r.OriginalPath == "scene.tsx").OriginalFileName.Should().Be("scene.tsx");

        fixture.Storage.Objects.Should().HaveCount(3);
        fixture.Link.LastPulledCommit.Should().Be("c1");
        fixture.Link.LastPulledFileCount.Should().Be(3);
    }

    [Fact]
    public async Task A_typescript_source_is_stored_as_text_not_as_a_transport_stream()
    {
        // Issue 8: the extension map calls every .ts video/vnd.dlna.mpeg-tts.
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", Tree(("web/proxy.ts", "export function proxy() { return 1; }\n"), ("vitest.config.mts", "export default {};\n")));

        await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        Pulled(fixture).Should().OnlyContain(r => r.MimeType == "text/plain");
    }

    [Fact]
    public async Task Pulling_the_same_commit_again_downloads_nothing()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", Tree(("a.txt", "one"), ("b.txt", "two")));

        await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);
        int uploads = fixture.Storage.Uploads;
        int blobs = fixture.GitHub.BlobRequests.Count;
        int trees = fixture.GitHub.TreeRequests.Count;

        GitHubPullOutcome second = await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d2", CancellationToken.None);

        second.Status.Should().Be("unchanged");
        fixture.Storage.Uploads.Should().Be(uploads, "a redelivered push must not duplicate work");
        fixture.GitHub.BlobRequests.Should().HaveCount(blobs);
        fixture.GitHub.TreeRequests.Should().HaveCount(trees, "the commit is already applied, so GitHub is not even asked");
        Pulled(fixture).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_later_commit_neither_rewrites_nor_removes_a_held_row()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", Tree(("a.txt", "one"), ("gone.txt", "bye")));
        await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);
        Dictionary<string, string> keys = Pulled(fixture).ToDictionary(f => f.OriginalPath!, f => f.StorageKey);
        foreach (ProjectFile row in Pulled(fixture)) row.OutputProducedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fixture.Db.Projects.Single(p => p.Id == fixture.ProjectId).CsamOutputHoldAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await fixture.Db.SaveChangesAsync();

        fixture.GitHub.AddTree("c2", Tree(("a.txt", "one changed")));
        GitHubPullOutcome outcome = await fixture.Service.PullAsync(fixture.Link, "c2", DateTime.UtcNow.AddMinutes(1), "d2", CancellationToken.None);

        outcome.Written.Should().Be(0);
        outcome.Deleted.Should().Be(0);
        Pulled(fixture).ToDictionary(f => f.OriginalPath!, f => f.StorageKey).Should().BeEquivalentTo(keys);
    }

    [Fact]
    public async Task A_later_commit_rewrites_what_changed_adds_what_is_new_and_removes_what_vanished()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", Tree(("a.txt", "one"), ("gone.txt", "bye")));
        await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);
        string goneKey = Pulled(fixture).Single(f => f.OriginalPath == "gone.txt").StorageKey;

        fixture.GitHub.AddTree("c2", Tree(("a.txt", "one changed"), ("new.txt", "hello")));
        GitHubPullOutcome outcome = await fixture.Service.PullAsync(fixture.Link, "c2", DateTime.UtcNow.AddMinutes(1), "d2", CancellationToken.None);

        outcome.Status.Should().Be("pulled");
        outcome.Written.Should().Be(2);
        outcome.Deleted.Should().Be(1);
        outcome.Unchanged.Should().Be(0);

        List<ProjectFile> rows = Pulled(fixture);
        rows.Select(r => r.OriginalPath).Should().Equal("a.txt", "new.txt");
        fixture.Storage.Objects.Should().NotContainKey(goneKey, "the removed file's object goes with its row");
    }

    [Fact]
    public async Task A_vanished_photo_a_picture_uses_leaves_the_picture_its_own_copy()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", Tree(("a.txt", "one"), ("gone.png", "pixels")));
        await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);
        ProjectFile gone = Pulled(fixture).Single(f => f.OriginalPath == "gone.png");
        string goneKey = gone.StorageKey;
        Guid pictureId = await PictureCopySeed.PictureOverAsync(fixture.Db, gone);

        fixture.GitHub.AddTree("c2", Tree(("a.txt", "one")));
        GitHubPullOutcome outcome = await fixture.Service.PullAsync(fixture.Link, "c2", DateTime.UtcNow.AddMinutes(1), "d2", CancellationToken.None);

        outcome.Deleted.Should().Be(1);
        fixture.Storage.Objects.Should().NotContainKey(goneKey);
        string copyKey = await PictureCopySeed.AssertKeptOwnCopyAsync(fixture.Db, fixture.ProjectId, pictureId, goneKey);
        fixture.Storage.Objects.Should().ContainKey(copyKey);
    }

    [Fact]
    public async Task An_unchanged_file_in_a_new_commit_is_not_downloaded_again()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", Tree(("keep.txt", "same"), ("change.txt", "before")));
        await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        fixture.GitHub.AddTree("c2", Tree(("keep.txt", "same"), ("change.txt", "after")));
        int uploads = fixture.Storage.Uploads;
        GitHubPullOutcome outcome = await fixture.Service.PullAsync(fixture.Link, "c2", DateTime.UtcNow.AddMinutes(1), "d2", CancellationToken.None);

        outcome.Written.Should().Be(1);
        outcome.Unchanged.Should().Be(1);
        fixture.Storage.Uploads.Should().Be(uploads + 1, "the blob sha is remembered, so only the changed file is fetched and stored");
    }

    [Fact]
    public async Task A_file_a_person_uploaded_is_never_overwritten_or_duplicated()
    {
        Fixture fixture = await NewAsync();
        fixture.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = fixture.ProjectId,
            OriginalFileName = "notes.txt",
            OriginalPath = "notes.txt",
            Category = "userFiles",
            StorageKey = "projects/" + fixture.ProjectId + "/userFiles/notes.txt",
            MimeType = "text/plain",
            SizeBytes = 4,
            UploadedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        fixture.GitHub.AddTree("c1", Tree(("notes.txt", "from the repo"), ("other.txt", "ok")));
        GitHubPullOutcome outcome = await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        outcome.Written.Should().Be(1);
        outcome.Skipped.Should().BeGreaterThan(0);

        List<ProjectFile> atPath = fixture.Db.ProjectFiles.Where(f => f.ProjectId == fixture.ProjectId && f.OriginalPath == "notes.txt").ToList();
        atPath.Should().ContainSingle("a pushed file must never take a path a person already owns");
        atPath[0].SourceKind.Should().BeNull();
        atPath[0].SizeBytes.Should().Be(4);
    }

    [Fact]
    public async Task An_older_push_that_arrives_after_a_newer_one_is_dropped()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c2", Tree(("a.txt", "newer")));
        await fixture.Service.PullAsync(fixture.Link, "c2", DateTime.UtcNow, "d2", CancellationToken.None);
        int uploads = fixture.Storage.Uploads;

        fixture.GitHub.AddTree("c1", Tree(("a.txt", "older")));
        GitHubPullOutcome outcome = await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow.AddMinutes(-10), "d1", CancellationToken.None);

        outcome.Status.Should().Be("stale");
        outcome.Written.Should().Be(0);
        fixture.Storage.Uploads.Should().Be(uploads, "the project mirrors ONE commit; a late retry of an older push must not move it backwards");
        fixture.Link.LastPulledCommit.Should().Be("c2");
    }

    [Fact]
    public async Task A_truncated_tree_is_refused_rather_than_half_applied()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.Trees["c1"] = new GitHubRepoTree(
            "c1",
            [new GitHubTreeEntry("a.txt", "blob", "sha-a", 1)],
            Truncated: true);

        Func<Task> act = () => fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        (await act.Should().ThrowAsync<GitHubSyncRefusedException>()).Which.Code.Should().Be("repository_too_large");
        fixture.Storage.Uploads.Should().Be(0, "a partial tree looks exactly like 'everything else was deleted'");
        Pulled(fixture).Should().BeEmpty();
    }

    [Fact]
    public async Task A_repository_over_the_file_limit_is_refused()
    {
        Fixture fixture = await NewAsync(new GitHubOptions { AppId = "1", PrivateKeyPem = "x", WebhookSecret = "s", MaxFilesPerPull = 2 });
        fixture.GitHub.AddTree("c1", Tree(("a", "1"), ("b", "2"), ("c", "3")));

        Func<Task> act = () => fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        GitHubSyncRefusedException refused = (await act.Should().ThrowAsync<GitHubSyncRefusedException>()).Which;
        refused.Code.Should().Be("too_many_files");
        refused.Actual.Should().Be(3);
        refused.Limit.Should().Be(2);
        fixture.Storage.Uploads.Should().Be(0);

        // The reason is on the link, in words the project's owner can act on — not a config key.
        fixture.Link.LastError.Should().Contain("Too many files").And.Contain("2").And.NotContain("MaxFilesPerPull");
    }

    [Fact]
    public async Task The_file_limit_counts_only_the_chosen_files_so_a_subset_of_a_large_repository_syncs()
    {
        Fixture fixture = await NewAsync(new GitHubOptions { AppId = "1", PrivateKeyPem = "x", WebhookSecret = "s", MaxFilesPerPull = 2 });
        fixture.Link.IncludePathsJson = GitHubIncludePaths.Serialize(["video"]);
        fixture.GitHub.AddTree("c1", Tree(
            ("video/scene.tsx", "1"), ("video/clip.txt", "2"),
            ("api/a.go", "3"), ("api/b.go", "4"), ("web/c.ts", "5")));

        GitHubPullOutcome outcome = await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        outcome.Status.Should().Be("pulled");
        outcome.Skipped.Should().Be(0, "files that were never chosen are not 'skipped'");
        Pulled(fixture).Select(f => f.OriginalPath).Should().Equal("video/clip.txt", "video/scene.tsx");
        fixture.Link.LastPulledFileCount.Should().Be(2);
        fixture.Link.LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_selection_of_single_files_and_folders_pulls_exactly_those_with_their_repository_paths()
    {
        Fixture fixture = await NewAsync();
        fixture.Link.IncludePathsJson = GitHubIncludePaths.Serialize(["README.md", "src/video"]);
        fixture.GitHub.AddTree("c1", Tree(
            ("README.md", "r"), ("LICENSE", "l"),
            ("src/video/scene.tsx", "s"), ("src/video/parts/title.tsx", "t"),
            ("src/videos/other.tsx", "not inside src/video"), ("src/app.ts", "a")));

        await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        Pulled(fixture).Select(f => f.OriginalPath).Should().Equal(
            "README.md", "src/video/parts/title.tsx", "src/video/scene.tsx");
    }

    [Fact]
    public async Task Narrowing_the_selection_and_forcing_a_sync_at_the_same_commit_removes_what_was_dropped()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.BranchHeads["acme/promo-video@main"] = "c1";
        fixture.GitHub.AddTree("c1", Tree(("video/scene.tsx", "s"), ("docs/guide.md", "g")));
        await fixture.Service.PullBranchAsync(fixture.Link, force: false, CancellationToken.None);
        Pulled(fixture).Should().HaveCount(2);

        fixture.Link.IncludePathsJson = GitHubIncludePaths.Serialize(["video"]);

        GitHubPullOutcome unforced = await fixture.Service.PullBranchAsync(fixture.Link, force: false, CancellationToken.None);
        unforced.Status.Should().Be("unchanged", "without force the same commit is a no-op");

        GitHubPullOutcome forced = await fixture.Service.PullBranchAsync(fixture.Link, force: true, CancellationToken.None);
        forced.Status.Should().Be("pulled");
        forced.Deleted.Should().Be(1);
        forced.Unchanged.Should().Be(1, "the file still chosen is not downloaded again");
        Pulled(fixture).Select(f => f.OriginalPath).Should().Equal("video/scene.tsx");
    }

    [Fact]
    public async Task A_missing_branch_is_recorded_on_the_link_in_plain_words()
    {
        Fixture fixture = await NewAsync(branch: "gone");

        Func<Task> act = () => fixture.Service.PullBranchAsync(fixture.Link, force: false, CancellationToken.None);

        await act.Should().ThrowAsync<GitHubApiException>();
        fixture.Link.LastError.Should().Contain("\"gone\"").And.Contain("Choose another branch");
    }

    [Fact]
    public async Task A_GitHub_refusal_is_recorded_without_REST_paths()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.TreeFailure = new GitHubApiException("GitHub answered 403 for /repos/acme/promo-video/git/trees/c1?recursive=1.", System.Net.HttpStatusCode.Forbidden);

        Func<Task> act = () => fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        await act.Should().ThrowAsync<GitHubApiException>();
        fixture.Link.LastError.Should().Contain("refused ReelBolt access").And.NotContain("/repos/");
    }

    [Fact]
    public async Task A_successful_sync_clears_the_previous_error()
    {
        Fixture fixture = await NewAsync();
        fixture.Link.LastError = "an earlier failure";
        fixture.GitHub.AddTree("c1", Tree(("a.txt", "1")));

        await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        fixture.Link.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Node_modules_and_the_git_directory_are_never_pulled()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddTree("c1", Tree((".git/config", "x"), ("node_modules/dep/index.js", "big"), ("src/app.ts", "ok")));

        GitHubPullOutcome outcome = await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        outcome.Written.Should().Be(1);
        Pulled(fixture).Select(f => f.OriginalPath).Should().Equal("src/app.ts");
    }

    [Fact]
    public async Task A_path_prefix_confines_the_pull_and_is_stripped_from_the_stored_paths()
    {
        Fixture fixture = await NewAsync(pathPrefix: "video");
        fixture.GitHub.AddTree("c1", Tree(("video/scene.tsx", "ok"), ("README.md", "outside"), ("video/nested/clip.txt", "ok")));

        await fixture.Service.PullAsync(fixture.Link, "c1", DateTime.UtcNow, "d1", CancellationToken.None);

        Pulled(fixture).Select(f => f.OriginalPath).Should().Equal("nested/clip.txt", "scene.tsx");
    }

    [Fact]
    public async Task Pulling_the_branch_head_uses_the_configured_branch()
    {
        Fixture fixture = await NewAsync(branch: "release/1");
        fixture.GitHub.BranchHeads["acme/promo-video@release/1"] = "head-sha";
        fixture.GitHub.AddTree("head-sha", Tree(("a.txt", "one")));

        GitHubPullOutcome outcome = await fixture.Service.PullBranchAsync(fixture.Link, force: false, CancellationToken.None);

        outcome.Status.Should().Be("pulled");
        outcome.Commit.Should().Be("head-sha");
        fixture.Link.Branch.Should().Be("release/1");
    }
}
