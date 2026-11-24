using Microsoft.AspNetCore.Http;
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
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
/// The repository picker and the folder browser, at the HTTP edge.
///
/// <para>
/// Both are new read paths that reach GitHub on the caller's behalf, so they are guarded the same way
/// every other project route is — the project access check first, and the caller's own connected
/// account second — and a project in another organization is <b>404</b> rather than 403.
/// </para>
/// </summary>
public class ProjectGitHubPickerTests
{
    private const string AccessToken = "ghu_token_value";

    private sealed class FakeCurrentUser(Guid userId, Guid organizationId, OrganizationRole role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "user@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
        public bool IsPlatformAdmin => false;
    }

    private sealed record Fixture(
        InferenceApiDbContext Db,
        ProjectGitHubController Controller,
        FakeGitHubAppClient GitHub,
        Guid OwnedProjectId,
        Guid ForeignProjectId,
        Guid OrganizationId);

    private static async Task<Fixture> NewAsync(bool connectAccount = true, bool userOAuthConfigured = true, OrganizationRole role = OrganizationRole.Owner)
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

        FakeGitHubAppClient gitHub = new() { IsConfigured = true, IsUserOAuthConfigured = userOAuthConfigured };
        gitHub.AddUserRepository(11, "acme", 1001, "acme/promo-video", "main");
        gitHub.AddTreeLevel("acme", "promo-video", "main",
            new GitHubTreeEntry("video", "tree", "sha-video", 0),
            new GitHubTreeEntry("README.md", "blob", "sha-readme", 4));
        gitHub.AddTreeLevel("acme", "promo-video", "sha-video",
            new GitHubTreeEntry("scene.tsx", "blob", "sha-scene", 10));

        FakeSecretProtector secrets = new();
        if (connectAccount)
        {
            db.GitHubAccountLinks.Add(new GitHubAccountLink
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OrganizationId = organizationId,
                GitHubUserId = 4242,
                Login = "octocat",
                ProtectedAccessToken = secrets.Protect(AccessToken)
            });
        }

        await db.SaveChangesAsync();
        tenant.BeginOrganizationScope(organizationId);

        GitHubOptions options = new()
        {
            AppId = "123456",
            PrivateKeyPem = "unused-in-tests",
            WebhookSecret = GitHubTestData.WebhookSecret,
            AppSlug = "reelbolt-test",
            ClientId = userOAuthConfigured ? "Iv1.abc" : null,
            ClientSecret = userOAuthConfigured ? "secret" : null,
            OAuthCallbackUrl = userOAuthConfigured ? "https://reel.example.com/api/v1/github/oauth/callback" : null
        };
        IOptions<GitHubOptions> configured = Microsoft.Extensions.Options.Options.Create(options);

        FakeCurrentUser currentUser = new(userId, organizationId, role);
        GitHubAccountService accounts = new(db, gitHub, configured, secrets, NullLogger<GitHubAccountService>.Instance);

        ProjectGitHubController controller = new(
            db,
            new ProjectAccessService(db, currentUser),
            new GitHubLinkService(db, gitHub, accounts, configured, NullLogger<GitHubLinkService>.Instance),
            new GitHubSyncQueue(),
            accounts,
            gitHub,
            currentUser,
            configured,
            new GitHubSyncThrottle(),
            NullLogger<ProjectGitHubController>.Instance);

        return new Fixture(db, controller, gitHub, owned.Id, foreign.Id, organizationId);
    }

    /// <summary>
    /// The payload of a successful picker answer. A controller action typed <c>ActionResult&lt;T&gt;</c>
    /// that returns <c>Ok(...)</c> puts the payload on <c>Result</c> and leaves <c>Value</c> null — the
    /// implicit conversion that fills <c>Value</c> only applies when the action returns the model itself.
    /// </summary>
    private static T BodyOf<T>(ActionResult<T> result) =>
        (T)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    [Fact]
    public async Task The_picker_lists_repositories_with_everything_the_UI_needs_to_offer_an_install()
    {
        Fixture fixture = await NewAsync();

        ActionResult<GitHubRepositoryListResponse> result = await fixture.Controller.Repositories(fixture.OwnedProjectId, CancellationToken.None);

        GitHubRepositoryListResponse body = BodyOf(result);
        body.Repositories.Should().HaveCount(1);

        GitHubRepositoryOption option = body.Repositories[0];
        option.FullName.Should().Be("acme/promo-video");
        option.IsPrivate.Should().BeTrue();
        option.DefaultBranch.Should().Be("main");
        option.LinkedProjectId.Should().BeNull();
        option.ManageUrl.Should().Be("https://github.com/settings/installations/11");

        // The App is installed on this repository but granted only selected ones, so the way to add
        // another is GitHub's own installation page — the picker cannot list what the app was never
        // given, because the person's token cannot see it either.
        option.RepositorySelection.Should().Be("selected");
        body.InstallUrl.Should().Be("https://github.com/apps/reelbolt-test/installations/new");
    }

    [Fact]
    public async Task The_picker_refuses_a_project_in_another_organization_with_404()
    {
        Fixture fixture = await NewAsync();

        ActionResult<GitHubRepositoryListResponse> result = await fixture.Controller.Repositories(fixture.ForeignProjectId, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>("a project in another organization is 404, never 403");
    }

    [Fact]
    public async Task The_picker_is_unavailable_when_no_github_account_is_connected()
    {
        Fixture fixture = await NewAsync(connectAccount: false);

        ActionResult<GitHubRepositoryListResponse> result = await fixture.Controller.Repositories(fixture.OwnedProjectId, CancellationToken.None);

        ObjectResult status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(409);
        status.Value.Should().BeEquivalentTo(new { error = "github_account_not_connected" });
    }

    [Fact]
    public async Task The_picker_says_so_when_the_deployment_cannot_offer_the_flow_at_all()
    {
        Fixture fixture = await NewAsync(userOAuthConfigured: false);

        ActionResult<GitHubRepositoryListResponse> result = await fixture.Controller.Repositories(fixture.OwnedProjectId, CancellationToken.None);

        ObjectResult status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(503);
        status.Value.Should().BeEquivalentTo(new { error = "github_oauth_not_configured" });
    }

    [Fact]
    public async Task The_folder_browser_lists_the_root_and_then_a_subdirectory()
    {
        Fixture fixture = await NewAsync();

        GitHubFolderListResponse root = BodyOf(await fixture.Controller.Tree(fixture.OwnedProjectId, 1001, null, CancellationToken.None));
        root.Path.Should().BeEmpty();
        root.Entries.Select(e => e.Name).Should().BeEquivalentTo(["video", "README.md"]);
        root.Entries.Single(e => e.Name == "video").IsDirectory.Should().BeTrue();

        GitHubFolderListResponse inside = BodyOf(await fixture.Controller.Tree(fixture.OwnedProjectId, 1001, "video", CancellationToken.None));
        inside.Path.Should().Be("video");
        inside.ParentPath.Should().BeEmpty();
        inside.Entries.Single().Path.Should().Be("video/scene.tsx");
    }

    [Fact]
    public async Task The_folder_browser_never_reads_a_repository_the_person_cannot_use()
    {
        Fixture fixture = await NewAsync();

        ActionResult<GitHubFolderListResponse> result = await fixture.Controller.Tree(fixture.OwnedProjectId, 999_999, null, CancellationToken.None);

        NotFoundObjectResult notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().BeEquivalentTo(new { error = "repository_not_found" });
        fixture.GitHub.TreeLevelRequests.Should().Be(0, "the installation token must never be spent on an unreachable repository");
    }

    [Fact]
    public async Task The_folder_browser_reports_a_folder_that_is_gone()
    {
        Fixture fixture = await NewAsync();

        ActionResult<GitHubFolderListResponse> result = await fixture.Controller.Tree(fixture.OwnedProjectId, 1001, "video/gone", CancellationToken.None);

        NotFoundObjectResult notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().BeEquivalentTo(new { error = "folder_not_found" });
    }

    [Fact]
    public async Task The_folder_browser_answers_503_not_502_when_only_the_oauth_half_is_configured()
    {
        // Staging, 2026-10-10: the OAuth client was set, so connecting and listing repositories worked,
        // but GITHUB_APP_ID / the private key were empty — every folder read answered 502 github_error.
        Fixture fixture = await NewAsync();
        fixture.GitHub.IsConfigured = false;

        ActionResult<GitHubFolderListResponse> result = await fixture.Controller.Tree(fixture.OwnedProjectId, 1001, null, CancellationToken.None);

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        refused.Value.Should().BeEquivalentTo(new { error = "github_app_not_configured" });
        fixture.GitHub.TreeLevelRequests.Should().Be(0);
    }

    [Fact]
    public async Task A_GitHub_failure_is_424_github_error_with_a_sentence_never_a_502_Cloudflare_would_mask()
    {
        // Staging, 2026-10-10: Cloudflare replaced the body of every origin 502 with its own HTML page, so the
        // dashboard showed an opaque "Bad Gateway" instead of github_error and its sentence.
        Fixture fixture = await NewAsync();
        fixture.GitHub.TreeLevelFailure = new GitHubApiException(
            "GitHub answered 500 for /repos/acme/promo-video/git/trees/main.", System.Net.HttpStatusCode.InternalServerError);

        ActionResult<GitHubFolderListResponse> result = await fixture.Controller.Tree(fixture.OwnedProjectId, 1001, null, CancellationToken.None);

        ObjectResult failed = result.Result.Should().BeOfType<ObjectResult>().Subject;
        failed.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        failed.Value.Should().BeEquivalentTo(new
        {
            error = "github_error",
            message = "GitHub answered 500 for /repos/acme/promo-video/git/trees/main."
        });
        ProjectGitHubController.GitHubFailureStatus.Should().Be(424);
        GitHubLinkService.FailureStatus.Should().Be(424);
    }

    [Fact]
    public async Task The_folder_browser_refuses_a_project_in_another_organization_with_404()
    {
        Fixture fixture = await NewAsync();

        ActionResult<GitHubFolderListResponse> result = await fixture.Controller.Tree(fixture.ForeignProjectId, 1001, null, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
