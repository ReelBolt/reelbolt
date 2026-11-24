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
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// Connecting a person's GitHub account, and what the resulting token is allowed to do
/// (docs/github-app.md, "Connecting a GitHub account").
///
/// <para>
/// These are the security-sensitive paths, so the assertions are about the properties that matter
/// rather than about call counts: the token is stored encrypted and never returned, a <c>state</c> is
/// spendable exactly once and only by the browser that started the flow, the picker shows exactly what
/// GitHub said the person may use and nothing else, and revocation destroys the credential rather than
/// ignoring it.
/// </para>
/// </summary>
public class GitHubAccountTests
{
    private const string AccessToken = "ghu_access_token_value_do_not_log";
    private const string RefreshToken = "ghr_refresh_token_value_do_not_log";
    private const string ClientSecret = "Iv1.client_secret_value";

    /// <summary>A clock the test drives, so an eight-hour token can expire in a millisecond.</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan delta) => Now = Now.Add(delta);
    }

    private sealed record Harness(
        InferenceApiDbContext Db,
        FakeGitHubAppClient GitHub,
        FakeSecretProtector Secrets,
        TestClock Clock,
        GitHubUserOAuthService OAuth,
        GitHubAccountService Accounts,
        Guid UserId,
        Guid OrganizationId);

    private static GitHubOptions Options(bool userOAuth = true) => new()
    {
        AppId = "123456",
        PrivateKeyPem = "unused-in-tests",
        WebhookSecret = GitHubTestData.WebhookSecret,
        AppSlug = "reelbolt-test",
        ClientId = userOAuth ? "Iv1.abc123" : null,
        ClientSecret = userOAuth ? ClientSecret : null,
        OAuthCallbackUrl = userOAuth ? "https://reel.example.com/api/v1/github/oauth/callback" : null,
        OAuthAuthorizeUrl = "https://github.com/login/oauth/authorize",
        OAuthTokenUrl = "https://github.com/login/oauth/access_token"
    };

    private static Harness New(
        GitHubOptions? options = null,
        bool userOAuthConfigured = true,
        ISecretProtector? secrets = null,
        DateTimeOffset? now = null)
    {
        TestTenant tenant = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid userId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(userId);

        FakeGitHubAppClient gitHub = new() { IsUserOAuthConfigured = userOAuthConfigured };
        FakeSecretProtector protector = new();
        TestClock clock = new(now ?? new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        IOptions<GitHubOptions> configured = Microsoft.Extensions.Options.Options.Create(options ?? Options());

        // Scoped services, so the tenant scope is entered per call exactly as a request would.
        tenant.BeginOrganizationScope(organizationId);

        return new Harness(
            db,
            gitHub,
            protector,
            clock,
            new GitHubUserOAuthService(db, gitHub, configured, secrets ?? protector, NullLogger<GitHubUserOAuthService>.Instance, clock),
            new GitHubAccountService(db, gitHub, configured, secrets ?? protector, NullLogger<GitHubAccountService>.Instance, clock),
            userId,
            organizationId);
    }

    /// <summary>Drives a full start → callback round trip and returns the completion.</summary>
    private static async Task<GitHubOAuthCompletion> ConnectAsync(Harness h, Guid? projectId = null)
    {
        GitHubOAuthStart start = (await h.OAuth.StartAsync(h.UserId, h.OrganizationId, projectId, CancellationToken.None))!;
        return await h.OAuth.CompleteAsync(start.State, start.Binding, "the-authorization-code", null, CancellationToken.None);
    }

    // ---------------------------------------------------------------- start

    [Fact]
    public async Task Starting_a_flow_asks_for_offline_access_and_pkce_and_never_for_a_scope_that_grants_permissions()
    {
        Harness h = New();

        GitHubOAuthStart start = (await h.OAuth.StartAsync(h.UserId, h.OrganizationId, null, CancellationToken.None))!;

        Uri url = new(start.AuthorizeUrl);
        url.GetLeftPart(UriPartial.Path).Should().Be("https://github.com/login/oauth/authorize");

        Dictionary<string, string> query = ParseQuery(url.Query);
        query["client_id"].Should().Be("Iv1.abc123");
        query["redirect_uri"].Should().Be("https://reel.example.com/api/v1/github/oauth/callback");
        query["state"].Should().Be(start.State);
        query["code_challenge_method"].Should().Be("S256");

        // The only scope a GitHub App supports. It does not grant permissions — the App's own
        // registration does — it asks for an expiring token with a refresh token.
        query["scope"].Should().Be("offline_access");

        // 43 base64url characters: a 32-byte verifier, which is what RFC 7636 requires for S256.
        query["code_challenge"].Length.Should().Be(43);
        query["code_challenge"].Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Fact]
    public async Task A_started_flow_stores_only_hashes_of_its_secrets()
    {
        Harness h = New();

        GitHubOAuthStart start = (await h.OAuth.StartAsync(h.UserId, h.OrganizationId, null, CancellationToken.None))!;

        GitHubOAuthState ticket = await h.Db.GitHubOAuthStates.SingleAsync();

        // Neither the state nor the binding cookie is recoverable from the row, so a database snapshot
        // cannot be turned back into a working callback URL.
        ticket.StateHash.Should().NotBe(start.State);
        ticket.BindingHash.Should().NotBe(start.Binding);
        ticket.StateHash.Should().Be(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(start.State))).ToLowerInvariant());

        // The PKCE verifier is a credential too, so it is stored protected rather than in the clear.
        ticket.ProtectedCodeVerifier.Should().NotContain(start.State);
        h.Secrets.TryUnprotect(ticket.ProtectedCodeVerifier, out string verifier).Should().BeTrue();
        verifier.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unconfigured_deployment_cannot_start_a_flow()
    {
        Harness h = New(Options(userOAuth: false), userOAuthConfigured: false);

        (await h.OAuth.StartAsync(h.UserId, h.OrganizationId, null, CancellationToken.None)).Should().BeNull();
        h.OAuth.IsConfigured.Should().BeFalse();
    }

    // ------------------------------------------------------------- callback

    [Fact]
    public async Task A_completed_flow_stores_the_token_encrypted_and_attributes_it_to_the_person_who_started_it()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, RefreshToken, h.Clock.GetUtcNow().UtcDateTime.AddHours(8), h.Clock.GetUtcNow().UtcDateTime.AddDays(180));
        h.GitHub.Identity = new GitHubUserIdentity(4242, "octocat", "https://avatars.example/octocat.png");

        Guid projectId = Guid.NewGuid();
        GitHubOAuthCompletion completion = await ConnectAsync(h, projectId);

        completion.Outcome.Should().Be(GitHubOAuthOutcome.Connected);
        completion.Login.Should().Be("octocat");
        completion.ProjectId.Should().Be(projectId, "the return path comes from our own ticket, never the request");

        GitHubAccountLink link = await h.Db.GitHubAccountLinks.SingleAsync();
        link.UserId.Should().Be(h.UserId);
        link.OrganizationId.Should().Be(h.OrganizationId);
        link.GitHubUserId.Should().Be(4242);
        link.IsActive.Should().BeTrue();

        // The column is ciphertext, and it is not merely obfuscated: the plaintext appears nowhere.
        link.ProtectedAccessToken.Should().NotBe(AccessToken);
        link.ProtectedAccessToken.Should().NotContain(AccessToken);
        h.Secrets.TryUnprotect(link.ProtectedAccessToken, out string decrypted).Should().BeTrue();
        decrypted.Should().Be(AccessToken);

        link.ProtectedRefreshToken.Should().NotBeNull();
        link.ProtectedRefreshToken!.Should().NotContain(RefreshToken);
    }

    [Fact]
    public async Task A_state_is_spendable_exactly_once()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);

        GitHubOAuthStart start = (await h.OAuth.StartAsync(h.UserId, h.OrganizationId, null, CancellationToken.None))!;

        GitHubOAuthCompletion first = await h.OAuth.CompleteAsync(start.State, start.Binding, "code", null, CancellationToken.None);
        GitHubOAuthCompletion replay = await h.OAuth.CompleteAsync(start.State, start.Binding, "code", null, CancellationToken.None);

        first.Outcome.Should().Be(GitHubOAuthOutcome.Connected);
        replay.Outcome.Should().Be(GitHubOAuthOutcome.InvalidState, "a replayed state must find no live ticket");
        h.GitHub.CodesExchanged.Should().HaveCount(1, "the replay must not reach GitHub at all");
    }

    [Fact]
    public async Task A_callback_without_the_browser_binding_cookie_is_refused_and_does_not_burn_the_ticket()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);

        GitHubOAuthStart start = (await h.OAuth.StartAsync(h.UserId, h.OrganizationId, null, CancellationToken.None))!;

        // Someone who learned the state — a shared screen, a leaked referrer — but is not the browser
        // that started the flow.
        GitHubOAuthCompletion attacker = await h.OAuth.CompleteAsync(start.State, "a-different-browser", "code", null, CancellationToken.None);
        attacker.Outcome.Should().Be(GitHubOAuthOutcome.StateMismatch);
        h.Db.GitHubAccountLinks.Should().BeEmpty();

        // And the legitimate browser is not locked out: a wrong cookie must not consume the ticket,
        // or the check would be a denial of service rather than a defence.
        GitHubOAuthCompletion real = await h.OAuth.CompleteAsync(start.State, start.Binding, "code", null, CancellationToken.None);
        real.Outcome.Should().Be(GitHubOAuthOutcome.Connected);
    }

    [Fact]
    public async Task A_callback_missing_any_of_its_three_parts_is_refused()
    {
        Harness h = New();
        GitHubOAuthStart start = (await h.OAuth.StartAsync(h.UserId, h.OrganizationId, null, CancellationToken.None))!;

        (await h.OAuth.CompleteAsync(null, start.Binding, "code", null, CancellationToken.None)).Outcome.Should().Be(GitHubOAuthOutcome.InvalidState);
        (await h.OAuth.CompleteAsync(start.State, null, "code", null, CancellationToken.None)).Outcome.Should().Be(GitHubOAuthOutcome.InvalidState);
        (await h.OAuth.CompleteAsync(start.State, start.Binding, null, null, CancellationToken.None)).Outcome.Should().Be(GitHubOAuthOutcome.InvalidState);
        h.GitHub.CodesExchanged.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_state_is_refused_and_spent()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);

        GitHubOAuthStart start = (await h.OAuth.StartAsync(h.UserId, h.OrganizationId, null, CancellationToken.None))!;
        h.Clock.Advance(TimeSpan.FromMinutes(11));

        GitHubOAuthCompletion completion = await h.OAuth.CompleteAsync(start.State, start.Binding, "code", null, CancellationToken.None);

        completion.Outcome.Should().Be(GitHubOAuthOutcome.ExpiredState);
        h.Db.GitHubAccountLinks.Should().BeEmpty();
        h.GitHub.CodesExchanged.Should().BeEmpty();
    }

    [Fact]
    public async Task A_person_who_cancels_on_github_is_told_so_rather_than_shown_an_error()
    {
        Harness h = New();
        GitHubOAuthStart start = (await h.OAuth.StartAsync(h.UserId, h.OrganizationId, null, CancellationToken.None))!;

        GitHubOAuthCompletion completion = await h.OAuth.CompleteAsync(start.State, start.Binding, null, "access_denied", CancellationToken.None);

        completion.Outcome.Should().Be(GitHubOAuthOutcome.Denied);
        h.Db.GitHubAccountLinks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_code_GitHub_will_not_redeem_creates_no_connection()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = null; // the fake throws, as GitHub answers 200 with an error body

        GitHubOAuthCompletion completion = await ConnectAsync(h);

        completion.Outcome.Should().Be(GitHubOAuthOutcome.ExchangeFailed);
        h.Db.GitHubAccountLinks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lost_key_ring_refuses_the_flow_rather_than_exchanging_without_the_verifier()
    {
        // The verifier protected at start cannot be read back, which is what a rotated dpkeys volume
        // looks like. Completing anyway would silently drop PKCE instead of reporting a broken install.
        Harness h = New(secrets: new UnreadableSecretProtector());
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);

        GitHubOAuthCompletion completion = await ConnectAsync(h);

        completion.Outcome.Should().Be(GitHubOAuthOutcome.ExchangeFailed);
        h.GitHub.CodesExchanged.Should().BeEmpty();
        h.Db.GitHubAccountLinks.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconnecting_clears_a_previous_revocation()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);

        await ConnectAsync(h);
        await h.Accounts.RevokeByGitHubUserIdAsync(1, CancellationToken.None);

        GitHubAccountLink revoked = await h.Db.GitHubAccountLinks.SingleAsync();
        revoked.IsActive.Should().BeFalse();
        revoked.ProtectedAccessToken.Should().BeEmpty("revocation destroys the credential rather than ignoring it");

        await ConnectAsync(h);

        GitHubAccountLink reconnected = await h.Db.GitHubAccountLinks.SingleAsync();
        reconnected.IsActive.Should().BeTrue();
        reconnected.RevokedReason.Should().BeNull();
    }

    // ------------------------------------------------------------- the view

    [Fact]
    public async Task Revoking_by_GitHub_account_id_hits_every_workspace_that_account_connected()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, RefreshToken, null, null);
        h.GitHub.Identity = new GitHubUserIdentity(99, "octocat", null);

        await ConnectAsync(h);

        // A second workspace, same person, same GitHub account.
        Guid otherOrg = Guid.NewGuid();
        h.Db.GitHubAccountLinks.Add(new GitHubAccountLink
        {
            Id = Guid.NewGuid(),
            UserId = h.UserId,
            OrganizationId = otherOrg,
            GitHubUserId = 99,
            Login = "octocat",
            ProtectedAccessToken = h.Secrets.Protect(AccessToken),
            ProtectedRefreshToken = h.Secrets.Protect(RefreshToken)
        });
        await h.Db.SaveChangesAsync();

        int revoked = await h.Accounts.RevokeByGitHubUserIdAsync(99, CancellationToken.None);

        revoked.Should().Be(2, "the person revoked ReelBolt, not one workspace's view of it");
        (await h.Db.GitHubAccountLinks.ToListAsync()).Should().OnlyContain(l => l.RevokedAt != null && l.ProtectedAccessToken == "");
        (await h.Db.GitHubAccountLinks.ToListAsync()).Should().OnlyContain(l => l.RevokedReason == "user_revoked");
    }

    [Fact]
    public async Task The_view_reports_the_state_and_never_a_token()
    {
        Harness h = New();

        GitHubAccountView before = await h.Accounts.GetViewAsync(h.UserId, h.OrganizationId, CancellationToken.None);
        before.Connected.Should().BeFalse();
        before.CanConnect.Should().BeTrue();
        before.InstallUrl.Should().Be("https://github.com/apps/reelbolt-test/installations/new");

        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);
        h.GitHub.Identity = new GitHubUserIdentity(7, "octocat", "https://avatars.example/o.png");
        await ConnectAsync(h);

        GitHubAccountView after = await h.Accounts.GetViewAsync(h.UserId, h.OrganizationId, CancellationToken.None);
        after.Connected.Should().BeTrue();
        after.Login.Should().Be("octocat");

        // The view type carries no token field at all; this asserts the intent, not just the value.
        typeof(GitHubAccountView).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_connection_in_another_workspace_is_invisible()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);
        await ConnectAsync(h);

        GitHubAccountView elsewhere = await h.Accounts.GetViewAsync(h.UserId, Guid.NewGuid(), CancellationToken.None);
        elsewhere.Connected.Should().BeFalse();

        await Assert.ThrowsAsync<GitHubAccountUnavailableException>(() =>
            h.Accounts.ListRepositoriesAsync(h.UserId, Guid.NewGuid(), CancellationToken.None));
    }

    // --------------------------------------------------------- the picker

    [Fact]
    public async Task The_picker_lists_exactly_what_GitHub_says_the_person_may_use()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);
        await ConnectAsync(h);

        h.GitHub.AddUserRepository(11, "acme", 1001, "acme/promo-video", "main", isPrivate: true);
        h.GitHub.AddUserRepository(11, "acme", 1002, "acme/site", "develop", isPrivate: false);
        h.GitHub.AddUserRepository(22, "reelbolt", 2001, "reelbolt/private-thing", "main", isPrivate: true, accountType: "Organization");

        IReadOnlyList<GitHubRepositoryChoice> choices = await h.Accounts.ListRepositoriesAsync(h.UserId, h.OrganizationId, CancellationToken.None);

        choices.Select(c => c.FullName).Should().BeEquivalentTo(["acme/promo-video", "acme/site", "reelbolt/private-thing"]);
        choices.Select(c => c.FullName).Should().NotContain("someone-else/secret");

        choices.Single(c => c.FullName == "acme/site").DefaultBranch.Should().Be("develop");
        choices.Single(c => c.FullName == "reelbolt/private-thing").AccountType.Should().Be("Organization");
        choices.Should().OnlyContain(c => c.LinkedProjectId == null);
    }

    [Fact]
    public async Task The_picker_annotates_this_workspaces_links_and_hides_another_workspaces()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);
        await ConnectAsync(h);

        h.GitHub.AddUserRepository(11, "acme", 1001, "acme/promo-video");
        h.GitHub.AddUserRepository(11, "acme", 1002, "acme/colleagues-repo");

        Guid mine = Guid.NewGuid();
        h.Db.ProjectGitHubLinks.Add(new ProjectGitHubLink
        {
            Id = Guid.NewGuid(),
            ProjectId = mine,
            OrganizationId = h.OrganizationId,
            InstallationId = 11,
            RepositoryId = 1001,
            RepositoryFullName = "acme/promo-video"
        });
        h.Db.ProjectGitHubLinks.Add(new ProjectGitHubLink
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            InstallationId = 11,
            RepositoryId = 1002,
            RepositoryFullName = "acme/colleagues-repo"
        });
        await h.Db.SaveChangesAsync();

        IReadOnlyList<GitHubRepositoryChoice> choices = await h.Accounts.ListRepositoriesAsync(h.UserId, h.OrganizationId, CancellationToken.None);

        choices.Single(c => c.Id == 1001).LinkedProjectId.Should().Be(mine.ToString());

        // Another organization's link is absent rather than explained: the link endpoint answers 404
        // for it, and a picker entry saying "taken" would leak what that 404 exists to hide.
        choices.Should().NotContain(c => c.Id == 1002);
    }

    [Fact]
    public async Task The_picker_refuses_without_a_connection_rather_than_returning_an_empty_list()
    {
        Harness h = New();

        GitHubAccountUnavailableException error = await Assert.ThrowsAsync<GitHubAccountUnavailableException>(() =>
            h.Accounts.ListRepositoriesAsync(h.UserId, h.OrganizationId, CancellationToken.None));

        error.Reason.Should().Be("not_connected");
        error.StatusCode.Should().Be(409);
    }

    // ------------------------------------------------------- the token

    [Fact]
    public async Task An_expired_token_is_refreshed_and_the_replacement_is_what_gets_spent()
    {
        Harness h = New();
        DateTime issued = h.Clock.GetUtcNow().UtcDateTime;
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, RefreshToken, issued.AddHours(8), issued.AddDays(180));
        await ConnectAsync(h);

        h.GitHub.TokensToRefresh = new GitHubUserTokens("ghu_second_access_token", "ghr_second_refresh_token", h.Clock.GetUtcNow().UtcDateTime.AddHours(8), h.Clock.GetUtcNow().UtcDateTime.AddDays(180));
        h.Clock.Advance(TimeSpan.FromHours(8));

        await h.Accounts.ListRepositoriesAsync(h.UserId, h.OrganizationId, CancellationToken.None);

        h.GitHub.RefreshTokensUsed.Should().Equal(new[] { RefreshToken });
        h.GitHub.UserTokensSeen.Should().Equal(
            new[] { "ghu_second_access_token" },
            "the refreshed token must be what is spent, not the expired one");

        GitHubAccountLink link = await h.Db.GitHubAccountLinks.SingleAsync();
        h.Secrets.TryUnprotect(link.ProtectedAccessToken, out string stored).Should().BeTrue();
        stored.Should().Be("ghu_second_access_token");
        link.ProtectedAccessToken.Should().NotContain("ghu_second_access_token");
    }

    [Fact]
    public async Task A_token_that_does_not_expire_is_never_refreshed()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null); // no expiry: the app has not opted in
        await ConnectAsync(h);

        await h.Accounts.ListRepositoriesAsync(h.UserId, h.OrganizationId, CancellationToken.None);

        h.GitHub.RefreshTokensUsed.Should().BeEmpty();
        h.GitHub.UserTokensSeen.Should().Equal(AccessToken);
    }

    [Fact]
    public async Task A_refused_refresh_revokes_the_connection_instead_of_retrying_forever()
    {
        Harness h = New();
        DateTime issued = h.Clock.GetUtcNow().UtcDateTime;
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, RefreshToken, issued.AddHours(8), issued.AddDays(180));
        await ConnectAsync(h);

        h.GitHub.TokensToRefresh = null; // GitHub refused: revoked, or the six-month refresh lapsed
        h.Clock.Advance(TimeSpan.FromHours(8));

        GitHubAccountUnavailableException error = await Assert.ThrowsAsync<GitHubAccountUnavailableException>(() =>
            h.Accounts.ListRepositoriesAsync(h.UserId, h.OrganizationId, CancellationToken.None));

        error.Reason.Should().Be("revoked");

        GitHubAccountLink link = await h.Db.GitHubAccountLinks.SingleAsync();
        link.IsActive.Should().BeFalse();
        link.ProtectedAccessToken.Should().BeEmpty();
        link.RevokedReason.Should().Be("invalid_grant");
    }

    [Fact]
    public async Task A_token_the_key_ring_cannot_read_revokes_the_connection()
    {
        Harness write = New();
        write.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);
        await ConnectAsync(write);

        // The same rows, read through a protector that cannot decrypt anything.
        InferenceApiDbContext db = write.Db;
        FakeGitHubAppClient gitHub = write.GitHub;
        IOptions<GitHubOptions> options = Microsoft.Extensions.Options.Options.Create(Options());
        GitHubAccountService service = new(db, gitHub, options, new UnreadableSecretProtector(), NullLogger<GitHubAccountService>.Instance, write.Clock);

        GitHubAccountUnavailableException error = await Assert.ThrowsAsync<GitHubAccountUnavailableException>(() =>
            service.ListRepositoriesAsync(write.UserId, write.OrganizationId, CancellationToken.None));

        error.Reason.Should().Be("unreadable");
        (await db.GitHubAccountLinks.SingleAsync()).ProtectedAccessToken.Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnecting_removes_the_credential_and_says_so()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);
        await ConnectAsync(h);

        (await h.Accounts.DisconnectAsync(h.UserId, h.OrganizationId, CancellationToken.None)).Should().BeTrue();

        // Disconnecting twice is idempotent rather than a 404: the row is kept so the dashboard can
        // still say "your GitHub authorization was revoked", and the caller's intent is already met.
        (await h.Accounts.DisconnectAsync(h.UserId, h.OrganizationId, CancellationToken.None)).Should().BeTrue();

        // A different caller with no row at all is the real "nothing to disconnect".
        (await h.Accounts.DisconnectAsync(Guid.NewGuid(), h.OrganizationId, CancellationToken.None)).Should().BeFalse();

        GitHubAccountLink link = await h.Db.GitHubAccountLinks.SingleAsync();
        link.RevokedReason.Should().Be("disconnected");
        link.ProtectedAccessToken.Should().BeEmpty();
    }

    // ------------------------------------------------------- the folder browser

    /// <summary>
    /// A connected account with one repository and a two-level tree. Connecting first is not
    /// decoration: the folder browser spends the person's own token, so without a connection there is
    /// nothing to browse with.
    /// </summary>
    private static async Task<Harness> WithRepositoryAsync(Harness h)
    {
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);
        await ConnectAsync(h);

        h.GitHub.AddUserRepository(11, "acme", 1001, "acme/promo-video", "main");

        h.GitHub.AddTreeLevel("acme", "promo-video", "main",
            new GitHubTreeEntry("video", "tree", "sha-video", 0),
            new GitHubTreeEntry("assets", "tree", "sha-assets", 0),
            new GitHubTreeEntry("README.md", "blob", "sha-readme", 12));

        h.GitHub.AddTreeLevel("acme", "promo-video", "sha-video",
            new GitHubTreeEntry("scene.tsx", "blob", "sha-scene", 100),
            new GitHubTreeEntry("clips", "tree", "sha-clips", 0));

        return h;
    }

    [Fact]
    public async Task The_root_listing_names_the_repository_root_and_marks_directories()
    {
        Harness h = await WithRepositoryAsync(New());

        GitHubFolderListing root = await h.Accounts.ListFolderAsync(h.UserId, h.OrganizationId, 1001, null, CancellationToken.None);

        root.Path.Should().BeEmpty();
        root.ParentPath.Should().BeNull();
        root.Entries.Should().HaveCount(3);

        // Directories first, then files; the picker offers only the directories.
        root.Entries[0].Should().BeEquivalentTo(new { Name = "assets", IsDirectory = true });
        root.Entries[1].IsDirectory.Should().BeTrue();
        root.Entries.Select(e => e.Path).Should().BeEquivalentTo(["video", "assets", "README.md"]);
        root.Entries.Single(e => e.Name == "README.md").IsDirectory.Should().BeFalse();
    }

    [Fact]
    public async Task Browsing_into_a_folder_resolves_its_children_from_the_branch_head()
    {
        Harness h = await WithRepositoryAsync(New());

        GitHubFolderListing listing = await h.Accounts.ListFolderAsync(h.UserId, h.OrganizationId, 1001, "video", CancellationToken.None);

        listing.Path.Should().Be("video");
        listing.ParentPath.Should().BeEmpty();
        listing.Entries.Select(e => e.Path).Should().BeEquivalentTo(["video/clips", "video/scene.tsx"]);
        listing.Entries.Single(e => e.Name == "clips").IsDirectory.Should().BeTrue();

        // One request per level descended, and both were made by the server: the client never sends a
        // tree SHA, so it cannot point the walk at another repository's subtree.
        h.GitHub.TreeLevelRequests.Should().Be(2);
    }

    [Fact]
    public async Task A_folder_that_is_not_there_is_a_not_found()
    {
        Harness h = await WithRepositoryAsync(New());

        // A folder that is not there is its own exception, not a repository that cannot be reached:
        // the picker says "that folder is gone", not "we cannot read that repository".
        await Assert.ThrowsAsync<GitHubFolderNotFoundException>(() =>
            h.Accounts.ListFolderAsync(h.UserId, h.OrganizationId, 1001, "video/nope", CancellationToken.None));
    }

    [Fact]
    public async Task A_repository_with_no_commits_browses_as_an_empty_folder()
    {
        Harness h = New();
        h.GitHub.TokensToIssue = new GitHubUserTokens(AccessToken, null, null, null);
        await ConnectAsync(h);

        // An empty repository has no tree, and GitHub answers 404 for the tree of a branch that does
        // not exist yet. That is "nothing to pull", not a failure.
        h.GitHub.AddUserRepository(11, "acme", 1001, "acme/brand-new", "main");

        GitHubFolderListing listing = await h.Accounts.ListFolderAsync(h.UserId, h.OrganizationId, 1001, null, CancellationToken.None);

        listing.Path.Should().BeEmpty();
        listing.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repository_the_person_cannot_use_is_never_read_from()
    {
        Harness h = await WithRepositoryAsync(New());

        GitHubApiException error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            h.Accounts.ListFolderAsync(h.UserId, h.OrganizationId, 999_999, null, CancellationToken.None));

        error.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        // The assertion that matters: the installation token was never used to read anything, so an
        // unreachable repository id cannot be turned into a file listing of a repository the app was
        // installed on but this person cannot see.
        h.GitHub.TreeLevelRequests.Should().Be(0);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("video/../../etc")]
    [InlineData("./video")]
    public async Task A_path_that_is_not_a_plain_folder_is_refused(string path)
    {
        Harness h = await WithRepositoryAsync(New());

        await Assert.ThrowsAsync<GitHubApiException>(() =>
            h.Accounts.ListFolderAsync(h.UserId, h.OrganizationId, 1001, path, CancellationToken.None));
    }

    [Fact]
    public async Task A_path_deeper_than_the_cap_is_refused_before_any_request()
    {
        Harness h = await WithRepositoryAsync(New());
        string deep = string.Join('/', Enumerable.Repeat("d", GitHubAccountService.MaxPathSegments + 1));

        GitHubApiException error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            h.Accounts.ListFolderAsync(h.UserId, h.OrganizationId, 1001, deep, CancellationToken.None));

        error.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        h.GitHub.TreeLevelRequests.Should().Be(0);
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
}
