#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// Choosing which files of a repository a project syncs, at the HTTP edge (docs/github-app.md
/// "Choosing which files are synced"): the selection a person makes is sent, validated, persisted and
/// used by every later sync; it can be changed afterwards with a re-sync; and every endpoint that
/// reaches the repository on the caller's behalf — browse, change, "Sync now" — applies the same person
/// check linking does, never the installation alone.
/// </summary>
public class ProjectGitHubSelectionTests
{
    private sealed class FakeCurrentUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "user@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
    }

    /// <summary>A clock a test moves by hand, so the sync cooldown is exercised without waiting.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Fixture(
        InferenceApiDbContext Db,
        ProjectGitHubController Controller,
        FakeGitHubAppClient GitHub,
        InMemoryFileStorage Storage,
        ManualClock Clock,
        GitHubSyncHarness Sync,
        Guid ProjectId,
        Guid ForeignProjectId,
        ReelBolt.WorkflowEngine.Tests.Services.Telemetry.RecordingProductEventEmitter Events,
        Guid UserId,
        Guid OrganizationId)
    {
        /// <summary>Lets the sync cooldown run out, as if the person waited.</summary>
        public void WaitOutCooldown() => Clock.Advance(GitHubSyncThrottle.Cooldown + TimeSpan.FromSeconds(1));

        /// <summary>
        /// Runs the queued background syncs, then forgets what the request's context tracked so the
        /// next read sees what the worker saved — as the next request would, with its own context.
        /// </summary>
        public async Task<int> DrainAsync()
        {
            int ran = await Sync.DrainAsync();
            Db.ChangeTracker.Clear();
            return ran;
        }

        /// <summary>The link as stored, read past the request context's tracked copy.</summary>
        public ProjectGitHubLink StoredLink() => Db.ProjectGitHubLinks.AsNoTracking().Single(l => l.ProjectId == ProjectId);
    }

    private static async Task<Fixture> NewAsync(
        bool connected = true,
        bool linked = false,
        string? includePathsJson = null,
        int maxFiles = 2000)
    {
        TestTenant tenant = new();
        string databaseName = $"github-selection-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant, databaseName, root);

        Guid userId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(userId);
        Guid foreignUserId = Guid.NewGuid();
        Guid foreignOrganizationId = TestTenancy.OrgOf(foreignUserId);

        Project owned = GitHubTestData.NewProject(organizationId, userId, "mine");
        Project foreign = GitHubTestData.NewProject(foreignOrganizationId, foreignUserId, "theirs");
        db.Projects.AddRange(owned, foreign);

        if (linked)
        {
            ProjectGitHubLink link = GitHubTestData.NewLink(owned.Id, organizationId, userId);
            link.IncludePathsJson = includePathsJson;
            db.ProjectGitHubLinks.Add(link);
        }

        await db.SaveChangesAsync();
        tenant.BeginOrganizationScope(organizationId);

        FakeGitHubAppClient gitHub = new() { IsConfigured = true, IsUserOAuthConfigured = true };
        gitHub.AddRepository(GitHubTestData.RepositoryFullName, GitHubTestData.InstallationId);
        gitHub.BranchHeads["acme/promo-video@main"] = "c1";
        gitHub.BranchHeads["acme/promo-video@release"] = "c2";
        gitHub.AddTree("c1", new Dictionary<string, string>
        {
            ["video/scene.tsx"] = "scene",
            ["video/parts/title.tsx"] = "title",
            ["README.md"] = "readme",
            ["api/main.go"] = "go",
        });
        gitHub.AddTree("c2", new Dictionary<string, string> { ["video/scene.tsx"] = "release scene" });

        GitHubOptions options = GitHubTestData.Configured();
        options.MaxFilesPerPull = maxFiles;
        IOptions<GitHubOptions> configured = Options.Create(options);

        FakeCurrentUser currentUser = new(userId, organizationId);
        ConnectedGitHubAccountService accounts = new(gitHub, connected);
        InMemoryFileStorage storage = new();
        ManualClock clock = new();
        GitHubSyncThrottle syncThrottle = new(clock);
        GitHubSyncHarness sync = new(tenant, () => GitHubTestDb.New(tenant, databaseName, root), gitHub, storage, configured);

        ReelBolt.WorkflowEngine.Tests.Services.Telemetry.RecordingProductEventEmitter events = new();
        ProjectGitHubController controller = new(
            db,
            new ProjectAccessService(db, currentUser),
            new GitHubLinkService(db, gitHub, accounts, configured, NullLogger<GitHubLinkService>.Instance),
            sync.Queue,
            accounts,
            gitHub,
            currentUser,
            configured,
            syncThrottle,
            NullLogger<ProjectGitHubController>.Instance,
            events)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return new Fixture(db, controller, gitHub, storage, clock, sync, owned.Id, foreign.Id, events, userId, organizationId);
    }

    private static int StatusOf<T>(ActionResult<T> result) => result.Result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200
    };

    private static T Body<T>(ActionResult<T> result) => (T)((ObjectResult)result.Result!).Value!;

    private static object? ErrorOf<T>(ActionResult<T> result) =>
        ((ObjectResult)result.Result!).Value!.GetType().GetProperty("error")!.GetValue(((ObjectResult)result.Result!).Value);

    private static List<string> PulledPaths(Fixture fixture) =>
        fixture.Db.ProjectFiles
            .AsNoTracking()
            .Where(f => f.ProjectId == fixture.ProjectId && f.SourceKind == "github")
            .AsEnumerable()
            .Select(f => f.OriginalPath!)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    // ------------------------------------------------------------ the first connected source (product analytics)

    [Fact]
    public async Task Linking_a_repository_records_that_the_person_connected_a_source_and_nothing_else_does()
    {
        // The welcome flow sends a person who starts from code here, not to the drop zone, so this link is
        // how they add material: activation.source_connected, asked for once per user by the emitter.
        Fixture fixture = await NewAsync(linked: false);

        StatusOf(await fixture.Controller.Link(
            fixture.ProjectId, new LinkGitHubRepositoryRequest(GitHubTestData.RepositoryFullName), CancellationToken.None)).Should().Be(200);

        fixture.Events.Firsts.Should().Equal((fixture.UserId, (Guid?)fixture.OrganizationId, "activation.source_connected"));
        fixture.Events.Appends.Should().BeEmpty();

        // The sync the link queued, a change of the selection, "Sync now" and an unlink are not the step.
        await fixture.DrainAsync();
        fixture.WaitOutCooldown();
        await fixture.Controller.Update(fixture.ProjectId, new UpdateGitHubLinkRequest(IncludePaths: ["video"]), CancellationToken.None);
        await fixture.DrainAsync();
        fixture.WaitOutCooldown();
        await fixture.Controller.Pull(fixture.ProjectId, CancellationToken.None);
        await fixture.DrainAsync();
        await fixture.Controller.Unlink(fixture.ProjectId, CancellationToken.None);
        fixture.Events.Firsts.Should().HaveCount(1, "only the person's own link is the step");
    }

    [Fact]
    public async Task A_refused_link_and_a_link_on_somebody_elses_project_record_nothing()
    {
        Fixture fixture = await NewAsync(linked: false);

        StatusOf(await fixture.Controller.Link(
            fixture.ProjectId, new LinkGitHubRepositoryRequest("acme/not-installed"), CancellationToken.None)).Should().NotBe(200);
        StatusOf(await fixture.Controller.Link(
            fixture.ForeignProjectId, new LinkGitHubRepositoryRequest(GitHubTestData.RepositoryFullName), CancellationToken.None)).Should().Be(404);

        fixture.Events.Firsts.Should().BeEmpty();
        fixture.Db.ProjectGitHubLinks.Count().Should().Be(0);
    }

    // ------------------------------------------------------------ linking with a selection

    [Fact]
    public async Task A_selection_sent_with_the_link_is_normalized_persisted_echoed_and_used_by_the_first_sync()
    {
        Fixture fixture = await NewAsync();

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Link(
            fixture.ProjectId,
            new LinkGitHubRepositoryRequest(GitHubTestData.RepositoryFullName, IncludePaths: ["/video/", "video/scene.tsx", "README.md"]),
            CancellationToken.None);

        StatusOf(result).Should().Be(200);
        ProjectGitHubLinkResponse body = Body(result);
        body.IncludePaths.Should().Equal("README.md", "video");
        body.Syncing.Should().BeTrue("the first sync is queued, not run inside the request");
        fixture.GitHub.TreeRequests.Should().BeEmpty();

        (await fixture.DrainAsync()).Should().Be(1);

        ProjectGitHubLink stored = fixture.StoredLink();
        GitHubIncludePaths.Parse(stored.IncludePathsJson).Should().Equal("README.md", "video");
        stored.PathPrefix.Should().BeNull();
        stored.LastPulledFileCount.Should().Be(3);
        Body(await fixture.Controller.Get(fixture.ProjectId, CancellationToken.None)).Syncing.Should().BeFalse();

        PulledPaths(fixture).Should().Equal("README.md", "video/parts/title.tsx", "video/scene.tsx");
    }

    [Fact]
    public async Task A_link_with_no_selection_follows_the_whole_repository()
    {
        Fixture fixture = await NewAsync();

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Link(
            fixture.ProjectId, new LinkGitHubRepositoryRequest(GitHubTestData.RepositoryFullName), CancellationToken.None);

        Body(result).IncludePaths.Should().BeNull();
        await fixture.DrainAsync();
        PulledPaths(fixture).Should().HaveCount(4);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("video/../../etc")]
    [InlineData("video//scene.tsx")]
    [InlineData("video\\scene.tsx")]
    [InlineData("./video")]
    public async Task A_selection_with_a_traversal_or_malformed_path_is_refused_and_nothing_is_linked(string bad)
    {
        Fixture fixture = await NewAsync();

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Link(
            fixture.ProjectId,
            new LinkGitHubRepositoryRequest(GitHubTestData.RepositoryFullName, IncludePaths: ["video", bad]),
            CancellationToken.None);

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("invalid_include_paths");
        fixture.Db.ProjectGitHubLinks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_selection_over_the_file_limit_still_links_and_reports_the_reason_on_the_link()
    {
        Fixture fixture = await NewAsync(maxFiles: 2);

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Link(
            fixture.ProjectId, new LinkGitHubRepositoryRequest(GitHubTestData.RepositoryFullName), CancellationToken.None);

        StatusOf(result).Should().Be(200, "the link was saved; the sync is what was refused");
        await fixture.DrainAsync();

        ProjectGitHubLinkResponse body = Body(await fixture.Controller.Get(fixture.ProjectId, CancellationToken.None));
        body.Syncing.Should().BeFalse();
        body.LastError.Should().Contain("Too many files").And.Contain("4").And.Contain("2");
        fixture.StoredLink().LastError.Should().Be(body.LastError, "the reason is persisted, not only returned");
        PulledPaths(fixture).Should().BeEmpty();
    }

    [Fact]
    public async Task Linking_without_a_connected_account_is_refused_409()
    {
        Fixture fixture = await NewAsync(connected: false);

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Link(
            fixture.ProjectId,
            new LinkGitHubRepositoryRequest(GitHubTestData.RepositoryFullName, IncludePaths: ["video"]),
            CancellationToken.None);

        StatusOf(result).Should().Be(409);
        ErrorOf(result).Should().Be("github_account_not_connected");
        fixture.GitHub.TreeRequests.Should().BeEmpty();
    }

    // ------------------------------------------------------------ changing the selection

    [Fact]
    public async Task Changing_the_selection_saves_it_and_resyncs_the_same_commit()
    {
        Fixture fixture = await NewAsync(linked: true);
        await fixture.Controller.Pull(fixture.ProjectId, CancellationToken.None);
        await fixture.DrainAsync();
        PulledPaths(fixture).Should().HaveCount(4);
        fixture.WaitOutCooldown();

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(IncludePaths: ["video/scene.tsx"]), CancellationToken.None);

        StatusOf(result).Should().Be(200);
        ProjectGitHubLinkResponse body = Body(result);
        body.IncludePaths.Should().Equal("video/scene.tsx");
        body.Syncing.Should().BeTrue();

        (await fixture.DrainAsync()).Should().Be(1, "the same commit is re-synced, forced, because the selection changed");
        PulledPaths(fixture).Should().Equal("video/scene.tsx");
    }

    [Fact]
    public async Task Changing_the_branch_validates_it_and_syncs_from_it()
    {
        Fixture fixture = await NewAsync(linked: true);

        ActionResult<ProjectGitHubLinkResponse> missing = await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(Branch: "nope"), CancellationToken.None);
        StatusOf(missing).Should().Be(404);
        ErrorOf(missing).Should().Be("branch_not_found");

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(Branch: "release"), CancellationToken.None);

        Body(result).Branch.Should().Be("release");
        await fixture.DrainAsync();
        fixture.StoredLink().LastPulledCommit.Should().Be("c2");
        PulledPaths(fixture).Should().Equal("video/scene.tsx");
    }

    [Fact]
    public async Task Resending_the_stored_selection_changes_nothing_and_forces_no_sync()
    {
        Fixture fixture = await NewAsync(linked: true, includePathsJson: GitHubIncludePaths.Serialize(["README.md", "video"]));

        for (int i = 0; i < 5; i++)
        {
            // Spelled differently each time, normalized to the same thing.
            ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Update(
                fixture.ProjectId,
                new UpdateGitHubLinkRequest(Branch: "", IncludePaths: ["/video/", "README.md", "video/scene.tsx"]),
                CancellationToken.None);

            StatusOf(result).Should().Be(200);
            Body(result).Syncing.Should().BeFalse();
        }

        (await fixture.DrainAsync()).Should().Be(0, "nothing was queued");
        fixture.GitHub.TreeRequests.Should().BeEmpty("an unchanged selection must never be a way to force a sync");
    }

    [Fact]
    public async Task Changing_the_selection_again_inside_the_cooldown_is_refused_429()
    {
        Fixture fixture = await NewAsync(linked: true);

        StatusOf(await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(IncludePaths: ["video"]), CancellationToken.None)).Should().Be(200);

        ActionResult<ProjectGitHubLinkResponse> again = await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(IncludePaths: ["README.md"]), CancellationToken.None);

        StatusOf(again).Should().Be(429);
        ErrorOf(again).Should().Be("sync_too_soon");
        await fixture.DrainAsync();
        fixture.GitHub.TreeRequests.Should().HaveCount(1, "alternating selections must not be an unthrottled way to force pulls");
        GitHubIncludePaths.Parse(fixture.StoredLink().IncludePathsJson).Should().Equal("video");

        fixture.WaitOutCooldown();
        StatusOf(await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(IncludePaths: ["README.md"]), CancellationToken.None)).Should().Be(200);
    }

    [Theory]
    [InlineData("../../..")]
    [InlineData("main/../../../../user")]
    [InlineData("./main")]
    [InlineData("feature//x")]
    [InlineData("main.lock")]
    [InlineData("-rf")]
    [InlineData("a b")]
    [InlineData("a%2F..")]
    public async Task A_branch_that_could_steer_the_GitHub_path_is_refused_before_any_GitHub_call(string bad)
    {
        Fixture fixture = await NewAsync(linked: true);

        ActionResult<GitHubFileListResponse> files = await fixture.Controller.Files(fixture.ProjectId, null, bad, CancellationToken.None);
        StatusOf(files).Should().Be(400);
        ErrorOf(files).Should().Be("invalid_branch");

        ActionResult<ProjectGitHubLinkResponse> update = await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(Branch: bad), CancellationToken.None);
        StatusOf(update).Should().Be(400);
        ErrorOf(update).Should().Be("invalid_branch");

        fixture.GitHub.TreeRequests.Should().BeEmpty();
        fixture.Db.ProjectGitHubLinks.Single().Branch.Should().BeNull();
    }

    [Fact]
    public async Task Linking_with_a_branch_that_could_steer_the_GitHub_path_is_refused()
    {
        Fixture fixture = await NewAsync();

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Link(
            fixture.ProjectId,
            new LinkGitHubRepositoryRequest(GitHubTestData.RepositoryFullName, Branch: "../../.."),
            CancellationToken.None);

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("invalid_branch");
        fixture.Db.ProjectGitHubLinks.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_selection_means_every_file_again()
    {
        Fixture fixture = await NewAsync(linked: true, includePathsJson: GitHubIncludePaths.Serialize(["README.md"]));

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(IncludePaths: []), CancellationToken.None);

        Body(result).IncludePaths.Should().BeNull();
        await fixture.DrainAsync();
        fixture.StoredLink().IncludePathsJson.Should().BeNull();
        PulledPaths(fixture).Should().HaveCount(4);
    }

    [Fact]
    public async Task Changing_the_selection_needs_the_callers_own_connected_account()
    {
        Fixture fixture = await NewAsync(connected: false, linked: true);

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(IncludePaths: ["video"]), CancellationToken.None);

        StatusOf(result).Should().Be(409);
        ErrorOf(result).Should().Be("github_account_not_connected");
        fixture.Db.ProjectGitHubLinks.Single().IncludePathsJson.Should().BeNull("a refused change writes nothing");
        fixture.GitHub.TreeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Changing_the_selection_of_a_project_in_another_organization_is_404()
    {
        Fixture fixture = await NewAsync(linked: true);

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Update(
            fixture.ForeignProjectId, new UpdateGitHubLinkRequest(IncludePaths: ["video"]), CancellationToken.None);

        StatusOf(result).Should().Be(404);
    }

    [Fact]
    public async Task A_change_to_a_link_whose_repository_the_account_reaches_through_another_installation_is_refused()
    {
        Fixture fixture = await NewAsync(linked: true);
        ProjectGitHubLink link = fixture.Db.ProjectGitHubLinks.Single();
        link.InstallationId = 999;
        await fixture.Db.SaveChangesAsync();

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Update(
            fixture.ProjectId, new UpdateGitHubLinkRequest(IncludePaths: ["video"]), CancellationToken.None);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("repository_not_found");
    }

    // ------------------------------------------------------------ "Sync now"

    [Fact]
    public async Task Sync_now_needs_the_callers_own_connected_account()
    {
        Fixture fixture = await NewAsync(connected: false, linked: true);

        ActionResult<GitHubSyncQueuedResponse> result = await fixture.Controller.Pull(fixture.ProjectId, CancellationToken.None);

        StatusOf(result).Should().Be(409);
        (await fixture.DrainAsync()).Should().Be(0, "a refused request queues nothing");
        fixture.GitHub.TreeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_now_answers_202_queued_and_the_worker_does_the_pull()
    {
        Fixture fixture = await NewAsync(linked: true);
        using CancellationTokenSource request = new();

        ActionResult<GitHubSyncQueuedResponse> result = await fixture.Controller.Pull(fixture.ProjectId, request.Token);

        StatusOf(result).Should().Be(202);
        Body(result).Status.Should().Be("queued");
        fixture.GitHub.TreeRequests.Should().BeEmpty("nothing is pulled inside the request");
        Body(await fixture.Controller.Get(fixture.ProjectId, CancellationToken.None)).Syncing.Should().BeTrue();

        // The browser giving up (nginx's 499) no longer cancels anything: the pull is not on its token.
        request.Cancel();
        (await fixture.DrainAsync()).Should().Be(1);

        PulledPaths(fixture).Should().HaveCount(4);
        ProjectGitHubLinkResponse after = Body(await fixture.Controller.Get(fixture.ProjectId, CancellationToken.None));
        after.Syncing.Should().BeFalse();
        after.LastPulledCommit.Should().Be("c1");
        after.LastPulledFileCount.Should().Be(4);
        after.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Sync_now_while_a_sync_is_queued_answers_202_already_syncing_and_queues_nothing_more()
    {
        Fixture fixture = await NewAsync(linked: true);

        StatusOf(await fixture.Controller.Pull(fixture.ProjectId, CancellationToken.None)).Should().Be(202);
        ActionResult<GitHubSyncQueuedResponse> second = await fixture.Controller.Pull(fixture.ProjectId, CancellationToken.None);

        StatusOf(second).Should().Be(202);
        Body(second).Status.Should().Be("already_syncing");
        (await fixture.DrainAsync()).Should().Be(1, "one sync per project at a time");
        fixture.GitHub.TreeRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Sync_now_twice_in_a_row_is_refused_with_retry_after()
    {
        Fixture fixture = await NewAsync(linked: true);

        StatusOf(await fixture.Controller.Pull(fixture.ProjectId, CancellationToken.None)).Should().Be(202);
        await fixture.DrainAsync();
        ActionResult<GitHubSyncQueuedResponse> second = await fixture.Controller.Pull(fixture.ProjectId, CancellationToken.None);

        StatusOf(second).Should().Be(429);
        ErrorOf(second).Should().Be("sync_too_soon");
        fixture.Controller.Response.Headers.RetryAfter.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Sync_now_over_the_file_limit_records_the_count_and_the_limit_on_the_link()
    {
        Fixture fixture = await NewAsync(linked: true, maxFiles: 2);

        ActionResult<GitHubSyncQueuedResponse> result = await fixture.Controller.Pull(fixture.ProjectId, CancellationToken.None);
        StatusOf(result).Should().Be(202);
        await fixture.DrainAsync();

        ProjectGitHubLinkResponse link = Body(await fixture.Controller.Get(fixture.ProjectId, CancellationToken.None));
        link.Syncing.Should().BeFalse();
        link.LastError.Should().Contain("Too many files").And.Contain("4").And.Contain("2").And.NotContain("GitHub had a problem");
        PulledPaths(fixture).Should().BeEmpty();
    }

    // ------------------------------------------------------------ browsing

    [Fact]
    public async Task The_file_listing_of_the_linked_repository_carries_sizes_and_the_limits()
    {
        Fixture fixture = await NewAsync(linked: true, maxFiles: 1234);

        ActionResult<GitHubFileListResponse> result = await fixture.Controller.Files(fixture.ProjectId, null, null, CancellationToken.None);

        GitHubFileListResponse body = Body(result);
        body.Branch.Should().Be("main");
        body.Commit.Should().Be("c1");
        body.MaxFiles.Should().Be(1234);
        body.Entries.Where(e => !e.IsDirectory).Select(e => e.Path).Should().BeEquivalentTo(
            ["video/scene.tsx", "video/parts/title.tsx", "README.md", "api/main.go"]);
        body.Entries.Single(e => e.Path == "README.md").Size.Should().Be(6);
    }

    [Fact]
    public async Task The_file_listing_of_a_picked_repository_follows_the_chosen_branch()
    {
        Fixture fixture = await NewAsync();

        ActionResult<GitHubFileListResponse> result = await fixture.Controller.Files(
            fixture.ProjectId, GitHubTestData.RepositoryId, "release", CancellationToken.None);

        Body(result).Commit.Should().Be("c2");

        ActionResult<GitHubFileListResponse> missing = await fixture.Controller.Files(
            fixture.ProjectId, GitHubTestData.RepositoryId, "nope", CancellationToken.None);
        StatusOf(missing).Should().Be(404);
        ErrorOf(missing).Should().Be("branch_not_found");
    }

    [Fact]
    public async Task Browsing_needs_the_callers_own_connected_account_even_for_the_linked_repository()
    {
        Fixture fixture = await NewAsync(connected: false, linked: true);

        StatusOf(await fixture.Controller.Files(fixture.ProjectId, null, null, CancellationToken.None)).Should().Be(409);
        StatusOf(await fixture.Controller.Branches(fixture.ProjectId, null, CancellationToken.None)).Should().Be(409);
        fixture.GitHub.TreeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Browsing_a_repository_the_account_cannot_see_is_404()
    {
        Fixture fixture = await NewAsync();

        ActionResult<GitHubFileListResponse> result = await fixture.Controller.Files(fixture.ProjectId, 424242, null, CancellationToken.None);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("repository_not_found");
    }

    [Fact]
    public async Task The_branch_list_puts_the_default_branch_first()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.Branches["acme/promo-video"] = ["zeta", "main", "alpha"];

        ActionResult<GitHubBranchListResponse> result = await fixture.Controller.Branches(
            fixture.ProjectId, GitHubTestData.RepositoryId, CancellationToken.None);

        Body(result).Branches.Should().Equal("main", "alpha", "zeta");
        Body(result).DefaultBranch.Should().Be("main");
    }
}
