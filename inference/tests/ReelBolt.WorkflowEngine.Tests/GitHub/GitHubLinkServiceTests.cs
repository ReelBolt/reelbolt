#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
/// Creating a repository link. Two authorities have to agree before one exists: the caller may edit the
/// project, and GitHub says the app is really installed on that repository.
///
/// <para>
/// The cross-tenant rule is the interesting one. GitHub can tell ReelBolt that the app is installed on
/// a repository, but not which ReelBolt organization installed it — so an installation is claimed by
/// the first organization to use it, and another organization asking for the same repository gets
/// <b>404</b>, the same answer as a repository that does not exist. Never a 403, which would confirm
/// the repository is there.
/// </para>
/// </summary>
public class GitHubLinkServiceTests
{
    private sealed class Fixture
    {
        public required InferenceApiDbContext Db { get; init; }
        public required FakeGitHubAppClient GitHub { get; init; }
        public required GitHubLinkService Service { get; init; }
        public required Guid ProjectId { get; init; }
        public required Guid OrganizationId { get; init; }
        public required Guid UserId { get; init; }
    }

    private static async Task<Fixture> NewAsync(bool configured = true)
    {
        TestTenant tenant = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid userId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(userId);
        Project project = GitHubTestData.NewProject(organizationId, userId);
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        FakeGitHubAppClient gitHub = new() { IsConfigured = configured };
        gitHub.AddRepository(GitHubTestData.RepositoryFullName, GitHubTestData.InstallationId);

        GitHubLinkService service = new(db, gitHub, new ConnectedGitHubAccountService(gitHub), Options.Create(GitHubTestData.Configured()), NullLogger<GitHubLinkService>.Instance);

        return new Fixture
        {
            Db = db,
            GitHub = gitHub,
            Service = service,
            ProjectId = project.Id,
            OrganizationId = organizationId,
            UserId = userId
        };
    }

    [Fact]
    public async Task Linking_an_installed_repository_creates_the_link_and_claims_the_installation()
    {
        Fixture fixture = await NewAsync();

        GitHubLinkResult result = await fixture.Service.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId,
            "https://github.com/acme/promo-video", branch: null, pathPrefix: null, CancellationToken.None);

        result.Link.Should().NotBeNull();
        result.Link!.RepositoryId.Should().Be(GitHubTestData.RepositoryId);
        result.Link.InstallationId.Should().Be(GitHubTestData.InstallationId);
        result.Link.Branch.Should().BeNull("null means 'the repository's default branch', which is what the webhook matches on");

        GitHubAppInstallation claim = await fixture.Db.GitHubAppInstallations.SingleAsync();
        claim.OrganizationId.Should().Be(fixture.OrganizationId);
        claim.InstallationId.Should().Be(GitHubTestData.InstallationId);
    }

    [Fact]
    public async Task An_unconfigured_deployment_refuses_to_link()
    {
        Fixture fixture = await NewAsync();

        GitHubLinkService unconfigured = new(
            fixture.Db,
            new FakeGitHubAppClient { IsConfigured = false },
            new NoopGitHubAccountService(),
            Options.Create(new GitHubOptions()),
            NullLogger<GitHubLinkService>.Instance);

        GitHubLinkResult result = await unconfigured.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/promo-video", null, null, CancellationToken.None);

        result.StatusCode.Should().Be(503);
        result.Error.Should().Be("github_app_not_configured");
        fixture.Db.ProjectGitHubLinks.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_repository_the_app_cannot_see_is_not_found()
    {
        Fixture fixture = await NewAsync();

        GitHubLinkResult result = await fixture.Service.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/does-not-exist", null, null, CancellationToken.None);

        result.StatusCode.Should().Be(404);
        result.Error.Should().Be("repository_not_found");
    }

    [Fact]
    public async Task A_repository_without_the_app_installed_is_not_found()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.Repositories["acme/not-installed"] = new GitHubRepositoryInfo(7, "acme/not-installed", "main");

        GitHubLinkResult result = await fixture.Service.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/not-installed", null, null, CancellationToken.None);

        // Not in the caller's own list (GitHub lists only installed repositories), so it is refused
        // before any app-credentialed lookup, with the tenancy rules' 404.
        result.StatusCode.Should().Be(404);
        result.Error.Should().Be("repository_not_found");
    }

    [Fact]
    public async Task Linking_requires_a_connected_github_account()
    {
        // The cross-tenant hole this closes: with no account connected, linking used to fall back to
        // the installation claim alone, so anyone in any workspace could link (and pull) a repository
        // from an installation nobody had claimed yet.
        Fixture fixture = await NewAsync();
        GitHubLinkService service = new(
            fixture.Db, fixture.GitHub, new NoopGitHubAccountService(),
            Options.Create(GitHubTestData.Configured()), NullLogger<GitHubLinkService>.Instance);

        GitHubLinkResult result = await service.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/promo-video", null, null, CancellationToken.None);

        result.StatusCode.Should().Be(409);
        result.Error.Should().Be("github_account_not_connected");
        fixture.Db.ProjectGitHubLinks.Count().Should().Be(0);
        fixture.Db.GitHubAppInstallations.Count().Should().Be(0, "an unconnected caller must not claim an installation");
    }

    [Fact]
    public async Task A_repository_outside_the_callers_own_account_is_not_found()
    {
        Fixture fixture = await NewAsync();
        FakeGitHubAppClient appSees = new();
        appSees.AddRepository("other/secret", GitHubTestData.InstallationId);
        FakeGitHubAppClient callerSees = new();
        callerSees.AddRepository(GitHubTestData.RepositoryFullName, GitHubTestData.InstallationId);
        GitHubLinkService service = new(
            fixture.Db, appSees, new ConnectedGitHubAccountService(callerSees),
            Options.Create(GitHubTestData.Configured()), NullLogger<GitHubLinkService>.Instance);

        GitHubLinkResult result = await service.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "other/secret", null, null, CancellationToken.None);

        result.StatusCode.Should().Be(404);
        result.Error.Should().Be("repository_not_found");
        fixture.Db.ProjectGitHubLinks.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_repository_under_another_organizations_installation_is_not_found()
    {
        Fixture fixture = await NewAsync();

        // Another organization got there first, with the same GitHub installation.
        fixture.Db.GitHubAppInstallations.Add(new GitHubAppInstallation
        {
            Id = Guid.NewGuid(),
            InstallationId = GitHubTestData.InstallationId,
            OrganizationId = Guid.NewGuid(),
            AccountLogin = "acme",
            ClaimedByUserId = Guid.NewGuid(),
            ClaimedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        GitHubLinkResult result = await fixture.Service.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/promo-video", null, null, CancellationToken.None);

        result.StatusCode.Should().Be(404, "never 403: a different status would confirm the repository and the installation exist");
        result.Error.Should().Be("github_app_not_installed");
        fixture.Db.ProjectGitHubLinks.Count().Should().Be(0, "a refused link writes nothing");
    }

    [Fact]
    public async Task A_second_link_in_the_same_organization_is_a_conflict()
    {
        Fixture fixture = await NewAsync();
        await fixture.Service.LinkAsync(fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/promo-video", null, null, CancellationToken.None);

        // Another project in the same organization wants the same repository.
        Project other = GitHubTestData.NewProject(fixture.OrganizationId, fixture.UserId, "other");
        fixture.Db.Projects.Add(other);
        await fixture.Db.SaveChangesAsync();

        GitHubLinkResult result = await fixture.Service.LinkAsync(
            other.Id, fixture.OrganizationId, fixture.UserId, "acme/promo-video", null, null, CancellationToken.None);

        result.StatusCode.Should().Be(409);
        result.Error.Should().Be("repository_already_linked");
    }

    [Fact]
    public async Task A_project_can_only_hold_one_link()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.AddRepository("acme/second", GitHubTestData.InstallationId);
        await fixture.Service.LinkAsync(fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/promo-video", null, null, CancellationToken.None);

        GitHubLinkResult result = await fixture.Service.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/second", null, null, CancellationToken.None);

        result.StatusCode.Should().Be(409);
        result.Error.Should().Be("project_already_linked");
    }

    [Fact]
    public async Task An_unknown_branch_is_refused_up_front()
    {
        Fixture fixture = await NewAsync();

        GitHubLinkResult result = await fixture.Service.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/promo-video", "no-such-branch", null, CancellationToken.None);

        result.StatusCode.Should().Be(404);
        result.Error.Should().Be("branch_not_found");
    }

    [Fact]
    public async Task A_known_branch_is_accepted_and_a_path_prefix_is_normalized()
    {
        Fixture fixture = await NewAsync();
        fixture.GitHub.BranchHeads["acme/promo-video@release/1"] = "sha";

        GitHubLinkResult result = await fixture.Service.LinkAsync(
            fixture.ProjectId, fixture.OrganizationId, fixture.UserId, "acme/promo-video", "release/1", "/video/./clips/", CancellationToken.None);

        result.Link.Should().NotBeNull();
        result.Link!.Branch.Should().Be("release/1");
        result.Link.PathPrefix.Should().Be("video/clips");
    }

    [Theory]
    [InlineData("acme/promo-video", "acme", "promo-video")]
    [InlineData("https://github.com/acme/promo-video", "acme", "promo-video")]
    [InlineData("https://github.com/acme/promo-video.git", "acme", "promo-video")]
    [InlineData("github.com/acme/promo-video", "acme", "promo-video")]
    [InlineData("  acme/promo-video  ", "acme", "promo-video")]
    public void Repository_references_are_parsed(string input, string owner, string name)
    {
        GitHubLinkService.TryParseRepository(input, out string parsedOwner, out string parsedName).Should().BeTrue();
        parsedOwner.Should().Be(owner);
        parsedName.Should().Be(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("acme")]
    [InlineData("acme/promo/video")]
    [InlineData("https://gitlab.com/acme/promo-video")]
    [InlineData("https://github.com.evil.example/acme/promo-video")]
    public void Anything_that_is_not_a_github_repository_is_refused(string input)
    {
        GitHubLinkService.TryParseRepository(input, out _, out _).Should().BeFalse();
    }
}

/// <summary>
/// The project-facing route. Every action authorizes through <c>IProjectAccessService</c> first, so a
/// project in another organization is 404 and the link can never be read or changed from outside the
/// organization that owns it.
/// </summary>
public class ProjectGitHubControllerTests
{
    private sealed class FakeCurrentUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "user@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Member;
    }

    private sealed record Fixture(
        InferenceApiDbContext Db,
        ProjectGitHubController Controller,
        Guid OwnProjectId,
        Guid ForeignProjectId,
        Guid ForeignLinkId);

    private static async Task<Fixture> NewAsync(bool linked = true, bool appConfigured = true)
    {
        TestTenant tenant = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid userId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(userId);
        Guid foreignUserId = Guid.NewGuid();
        Guid foreignOrganizationId = TestTenancy.OrgOf(foreignUserId);

        Project owned = GitHubTestData.NewProject(organizationId, userId, "mine");
        Project foreign = GitHubTestData.NewProject(foreignOrganizationId, foreignUserId, "theirs");
        db.Projects.AddRange(owned, foreign);

        ProjectGitHubLink foreignLink = GitHubTestData.NewLink(foreign.Id, foreignOrganizationId, foreignUserId);
        db.ProjectGitHubLinks.Add(foreignLink);

        if (linked) db.ProjectGitHubLinks.Add(GitHubTestData.NewLink(owned.Id, organizationId, userId));

        await db.SaveChangesAsync();
        tenant.BeginOrganizationScope(organizationId);

        FakeCurrentUser currentUser = new(userId, organizationId);
        ProjectAccessService access = new(db, currentUser);

        FakeGitHubAppClient gitHub = new() { IsConfigured = appConfigured };
        gitHub.AddRepository(GitHubTestData.RepositoryFullName, GitHubTestData.InstallationId);

        GitHubLinkService links = new(db, gitHub, new ConnectedGitHubAccountService(gitHub), Options.Create(GitHubTestData.Configured()), NullLogger<GitHubLinkService>.Instance);

        ProjectGitHubController controller = new(
            db,
            access,
            links,
            new GitHubSyncQueue(),
            new NoopGitHubAccountService(),
            gitHub,
            currentUser,
            Options.Create(GitHubTestData.Configured()),
            new GitHubSyncThrottle(),
            NullLogger<ProjectGitHubController>.Instance);

        return new Fixture(db, controller, owned.Id, foreign.Id, foreignLink.Id);
    }

    private static int StatusOf<T>(ActionResult<T> result) => StatusOf((IActionResult?)result.Result);

    private static int StatusOf(IActionResult? result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 200,
        StatusCodeResult statusCode => statusCode.StatusCode,
        null => 200,
        _ => 200
    };

    [Fact]
    public async Task Reading_the_link_of_a_foreign_project_is_not_found()
    {
        Fixture fixture = await NewAsync();

        int status = StatusOf(await fixture.Controller.Get(fixture.ForeignProjectId, CancellationToken.None));

        status.Should().Be(404, "a project in another organization is 404, never 403");
    }

    [Fact]
    public async Task Reading_the_link_of_an_unlinked_project_is_not_found()
    {
        Fixture fixture = await NewAsync(linked: false);

        int status = StatusOf(await fixture.Controller.Get(fixture.OwnProjectId, CancellationToken.None));

        status.Should().Be(404);
    }

    [Fact]
    public async Task Reading_the_link_of_the_own_project_returns_it()
    {
        Fixture fixture = await NewAsync();

        ActionResult<ProjectGitHubLinkResponse> result = await fixture.Controller.Get(fixture.OwnProjectId, CancellationToken.None);

        StatusOf(result).Should().Be(200);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ProjectGitHubLinkResponse body = ok.Value.Should().BeOfType<ProjectGitHubLinkResponse>().Subject;
        body.Repository.Should().Be(GitHubTestData.RepositoryFullName);
        body.Branch.Should().Be("main");
    }

    [Fact]
    public async Task Linking_a_foreign_project_is_not_found_and_writes_nothing()
    {
        Fixture fixture = await NewAsync(linked: false);

        int before = fixture.Db.ProjectGitHubLinks.Count();
        int status = StatusOf(await fixture.Controller.Link(
            fixture.ForeignProjectId, new LinkGitHubRepositoryRequest("acme/promo-video"), CancellationToken.None));

        status.Should().Be(404);
        fixture.Db.ProjectGitHubLinks.Count().Should().Be(before);
    }

    [Fact]
    public async Task Unlinking_a_foreign_project_is_not_found_and_leaves_the_link_alone()
    {
        Fixture fixture = await NewAsync();

        int status = StatusOf(await fixture.Controller.Unlink(fixture.ForeignProjectId, CancellationToken.None));

        status.Should().Be(404);
        (await fixture.Db.ProjectGitHubLinks.AnyAsync(l => l.Id == fixture.ForeignLinkId)).Should().BeTrue();
    }

    [Fact]
    public async Task Unlinking_the_own_project_removes_the_link_and_keeps_its_files()
    {
        Fixture fixture = await NewAsync();
        fixture.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = fixture.OwnProjectId,
            OriginalFileName = "pulled.txt",
            OriginalPath = "pulled.txt",
            Category = "userFiles",
            SourceKind = "github",
            StorageKey = "k",
            MimeType = "text/plain"
        });
        await fixture.Db.SaveChangesAsync();

        StatusOf(await fixture.Controller.Unlink(fixture.OwnProjectId, CancellationToken.None)).Should().Be(204);

        fixture.Db.ProjectGitHubLinks.Count().Should().Be(1, "only the foreign project's link is left");
        fixture.Db.ProjectFiles.Count(f => f.ProjectId == fixture.OwnProjectId).Should().Be(1,
            "detaching a repository must not silently delete the source tree it pulled");
    }

    [Fact]
    public async Task Linking_when_the_app_is_not_configured_answers_503()
    {
        Fixture fixture = await NewAsync(linked: false, appConfigured: false);

        int status = StatusOf(await fixture.Controller.Link(
            fixture.OwnProjectId, new LinkGitHubRepositoryRequest("acme/promo-video"), CancellationToken.None));

        status.Should().Be(503);
    }

    [Fact]
    public async Task Pulling_an_unlinked_project_is_not_found()
    {
        Fixture fixture = await NewAsync(linked: false);

        int status = StatusOf(await fixture.Controller.Pull(fixture.OwnProjectId, CancellationToken.None));

        status.Should().Be(404);
    }
}
