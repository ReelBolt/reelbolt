#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// The real <see cref="GitHubAppClient"/>, over a captured transport.
///
/// <para>
/// Everything else in this folder drives a fake client, which is right for the authorization and
/// tenancy rules — but a fake agrees with whatever the test author believed GitHub's JSON looks like.
/// These tests exist because that belief was wrong in two places: <c>GET /repos/{o}/{r}</c> answers
/// <c>full_name</c>/<c>default_branch</c> and <c>GET /repos/{o}/{r}/installation</c> nests the login
/// under <c>account</c>, and the records were being deserialized straight from that JSON, so both
/// would have come back null on a real deployment. Nothing caught it because the App has never been
/// configured anywhere, so this code had never once spoken to api.github.com.
/// </para>
/// </summary>
public class GitHubAppClientTransportTests
{
    private const string ClientSecret = "the-client-secret";

    private static GitHubOptions Options(int maxListed = 500) => new()
    {
        AppId = "123456",
        PrivateKeyPem = TestPrivateKey(),
        WebhookSecret = "s",
        AppSlug = "reelbolt-test",
        ClientId = "Iv1.abc123",
        ClientSecret = ClientSecret,
        OAuthCallbackUrl = "https://reel.example.com/api/v1/github/oauth/callback",
        ApiBaseUrl = "https://api.github.com",
        OAuthAuthorizeUrl = "https://github.com/login/oauth/authorize",
        OAuthTokenUrl = "https://github.com/login/oauth/access_token",
        MaxRepositoriesListed = maxListed
    };

    private static string TestPrivateKey()
    {
        using RSA rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static (GitHubAppClient Client, CapturingHttpHandler Handler) New(HttpResponseMessage? installationToken = null)
    {
        CapturingHttpHandler handler = new();
        handler.Responder = (request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                return handler.Json(installationToken is null
                    ? "{\"token\":\"ghs_installation_token\",\"expires_at\":\"2099-01-01T00:00:00Z\"}"
                    : "{}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        return (new GitHubAppClient(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<GitHubAppClient>.Instance), handler);
    }

    [Fact]
    public async Task A_repository_is_read_from_GitHubs_own_field_names()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/installation", StringComparison.Ordinal))
                return handler.Json("{\"id\":4242,\"repository_selection\":\"all\",\"account\":{\"login\":\"acme\"}}");
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
                return handler.Json("{\"token\":\"ghs_installation_token\",\"expires_at\":\"2099-01-01T00:00:00Z\"}");

            // Like GitHub: a /repos route refuses the app JWT and answers only an installation token.
            return request.Headers.Authorization?.Parameter == "ghs_installation_token"
                ? handler.Json("{\"id\":77,\"full_name\":\"Acme/Promo-Video\",\"default_branch\":\"trunk\"}")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        };

        GitHubRepositoryInfo? info = await client.GetRepositoryAsync("acme", "promo-video", CancellationToken.None);

        info.Should().NotBeNull();

        // Both assertions are chosen so a broken mapping cannot pass by accident: the default branch has
        // no fallback, and the full name differs in case from the owner/name we asked for, so a value
        // produced by falling back to the request would not match.
        info!.DefaultBranch.Should().Be("trunk");
        info.FullName.Should().Be("Acme/Promo-Video");
        info.Id.Should().Be(77);
    }

    [Fact]
    public async Task An_installation_is_read_from_the_nested_account_object()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (_, _) => handler.Json("{\"id\":4242,\"repository_selection\":\"selected\",\"account\":{\"login\":\"acme\",\"type\":\"Organization\"}}");

        GitHubInstallationInfo? info = await client.GetInstallationForRepositoryAsync("acme", "promo-video", CancellationToken.None);

        info.Should().NotBeNull();
        info!.Id.Should().Be(4242);
        info.AccountLogin.Should().Be("acme");
        info.RepositorySelection.Should().Be("selected");
    }

    [Fact]
    public async Task The_person_is_read_from_user()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (_, _) => handler.Json("{\"id\":9,\"login\":\"octocat\",\"avatar_url\":\"https://avatars.example/o.png\"}");

        GitHubUserIdentity? identity = await client.GetUserAsync("ghu_token", CancellationToken.None);

        identity.Should().Be(new GitHubUserIdentity(9, "octocat", "https://avatars.example/o.png"));
    }

    [Fact]
    public async Task The_authorization_code_exchange_is_a_form_post_with_the_client_secret_in_the_body()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (_, _) => handler.Json("{\"access_token\":\"ghu_new\",\"refresh_token\":\"ghr_new\",\"expires_in\":28800,\"refresh_token_expires_in\":15811200,\"token_type\":\"bearer\"}");

        GitHubUserTokens tokens = await client.ExchangeUserCodeAsync("the-code", "the-verifier", CancellationToken.None);

        CapturingHttpHandler.CapturedRequest request = handler.Requests.Single();
        request.Method.Should().Be("POST");
        request.Url.Should().Be("https://github.com/login/oauth/access_token");

        // The secret must not travel in a URL, which is the part of a request that lands in access logs.
        request.Url.Should().NotContain(ClientSecret);
        request.Body.Should().Contain("client_secret=" + ClientSecret);
        request.Body.Should().Contain("code=the-code");
        request.Body.Should().Contain("code_verifier=the-verifier");
        request.Body.Should().Contain("redirect_uri=");

        // This call IS the authentication; an Authorization header would be the app JWT, which GitHub
        // does not accept here.
        request.Authorization.Should().BeNull();

        tokens.AccessToken.Should().Be("ghu_new");
        tokens.RefreshToken.Should().Be("ghr_new");
        tokens.AccessTokenExpiresAt.Should().NotBeNull();
        tokens.AccessTokenExpiresAt!.Value.Should().BeAfter(DateTime.UtcNow.AddHours(7));
    }

    [Fact]
    public async Task An_error_body_is_a_refusal_rather_than_a_null_token()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (_, _) => handler.Json("{\"error\":\"bad_verification_code\",\"error_description\":\"The code passed is incorrect or expired.\"}");

        GitHubApiException error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.ExchangeUserCodeAsync("stale-code", "verifier", CancellationToken.None));

        // GitHub's own code, and explicitly not its prose description, which is remote text.
        error.Message.Should().Contain("bad_verification_code");
        error.Message.Should().NotContain("The code passed is incorrect");
    }

    [Fact]
    public async Task A_refused_refresh_is_null_rather_than_an_exception()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (_, _) => handler.Json("{\"error\":\"bad_refresh_token\"}");

        (await client.RefreshUserTokenAsync("ghr_stale", CancellationToken.None)).Should().BeNull();

        handler.Requests.Single().Body.Should().Contain("grant_type=refresh_token");
    }

    [Fact]
    public async Task A_refresh_returns_the_rotated_pair()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (_, _) => handler.Json("{\"access_token\":\"ghu_2\",\"refresh_token\":\"ghr_2\",\"expires_in\":28800,\"refresh_token_expires_in\":100}");

        GitHubUserTokens? tokens = await client.RefreshUserTokenAsync("ghr_1", CancellationToken.None);

        tokens!.AccessToken.Should().Be("ghu_2");
        tokens.RefreshToken.Should().Be("ghr_2");
    }

    [Fact]
    public async Task Installations_are_read_from_the_installations_array()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (_, _) => handler.Json("{\"total_count\":2,\"installations\":[{\"id\":11,\"repository_selection\":\"all\",\"account\":{\"login\":\"acme\",\"type\":\"User\"}},{\"id\":22,\"repository_selection\":\"selected\",\"account\":{\"login\":\"reelbolt\",\"type\":\"Organization\"}}]}");

        IReadOnlyList<GitHubUserInstallation> installations = await client.ListUserInstallationsAsync("ghu_token", CancellationToken.None);

        installations.Should().HaveCount(2);
        installations[0].Should().BeEquivalentTo(new { Id = 11L, AccountLogin = "acme", AccountType = "User", RepositorySelection = "all" });
        installations[1].Should().BeEquivalentTo(new { Id = 22L, AccountLogin = "reelbolt", AccountType = "Organization" });

        handler.Requests.Single().Url.Should().Contain("/user/installations");
        handler.Requests.Single().Authorization.Should().Be("ghu_token");
    }

    [Fact]
    public async Task A_full_page_is_followed_to_the_next_one()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (request, _) =>
        {
            bool secondPage = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal);
            string body = secondPage
                ? "{\"total_count\":101,\"installations\":[{\"id\":999,\"account\":{\"login\":\"last\",\"type\":\"User\"}}]}"
                : "{\"total_count\":101,\"installations\":[" + string.Join(',', Enumerable.Range(1, 100).Select(i => $"{{\"id\":{i},\"account\":{{\"login\":\"a{i}\",\"type\":\"User\"}}}}")) + "]}";
            return handler.Json(body);
        };

        IReadOnlyList<GitHubUserInstallation> installations = await client.ListUserInstallationsAsync("ghu_token", CancellationToken.None);

        installations.Should().HaveCount(101);
        installations.Last().AccountLogin.Should().Be("last");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Url.Should().Contain("page=2");
    }

    [Fact]
    public async Task A_repository_list_is_bounded_so_one_dashboard_request_cannot_run_away()
    {
        CapturingHttpHandler handler = new();
        handler.Responder = (_, _) => handler.Json("{\"total_count\":1000,\"repositories\":[" + string.Join(',', Enumerable.Range(1, 100).Select(i => $"{{\"id\":{i},\"full_name\":\"a/r{i}\",\"default_branch\":\"main\",\"private\":true}}")) + "]}");

        GitHubOptions options = Options(maxListed: 100);
        GitHubAppClient client = new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(options), NullLogger<GitHubAppClient>.Instance);

        IReadOnlyList<GitHubUserRepository> repositories = await client.ListInstallationRepositoriesAsync("ghu_token", 11, "acme", CancellationToken.None);

        handler.Requests.Should().HaveCount(1, "the cap stops the walk after the first page");
        repositories.Should().HaveCount(100);
        repositories[0].Should().BeEquivalentTo(new { Id = 1L, FullName = "a/r1", DefaultBranch = "main", IsPrivate = true, InstallationId = 11L, AccountLogin = "acme" });
    }

    [Fact]
    public async Task A_tree_level_is_not_recursive_and_is_read_with_an_installation_token()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
                return handler.Json("{\"token\":\"ghs_installation_token\",\"expires_at\":\"2099-01-01T00:00:00Z\"}");

            return handler.Json("{\"sha\":\"tree-sha\",\"truncated\":false,\"tree\":[{\"path\":\"video\",\"type\":\"tree\",\"sha\":\"s1\"},{\"path\":\"README.md\",\"type\":\"blob\",\"sha\":\"s2\",\"size\":42}]}");
        };

        GitHubTreeLevel level = await client.GetTreeLevelAsync(4242, "acme", "promo-video", "main", CancellationToken.None);

        level.Entries.Should().HaveCount(2);
        level.Truncated.Should().BeFalse();

        CapturingHttpHandler.CapturedRequest tree = handler.Requests.Single(r => r.Url.Contains("/git/trees/", StringComparison.Ordinal));
        // recursive=1 would return the whole repository and can be truncated; a folder picker walks one
        // level at a time instead.
        tree.Url.Should().NotContain("recursive");
        tree.Authorization.Should().Be("ghs_installation_token");
    }

    [Fact]
    public async Task Installation_tokens_are_requested_with_a_user_agent_every_time()
    {
        // Two regressions found on staging: GitHub answers 403 to a request with no User-Agent, and the
        // app JWT's RSA key used to be disposed after signing while IdentityModel kept a cached signer
        // pointing at it, so every token request after the first threw ObjectDisposedException.
        List<string?> agents = [];
        CapturingHttpHandler handler = new();
        handler.Responder = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                agents.Add(request.Headers.UserAgent.ToString());
                return handler.Json("{\"token\":\"ghs_t\",\"expires_at\":\"2099-01-01T00:00:00Z\"}");
            }

            return handler.Json("{\"sha\":\"t\",\"truncated\":false,\"tree\":[]}");
        };
        GitHubAppClient client = new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<GitHubAppClient>.Instance);

        // Different installations, so each one mints a fresh app JWT.
        await client.GetTreeLevelAsync(1, "acme", "a", "main", CancellationToken.None);
        await client.GetTreeLevelAsync(2, "acme", "b", "main", CancellationToken.None);
        await client.GetTreeLevelAsync(3, "acme", "c", "main", CancellationToken.None);

        agents.Should().HaveCount(3).And.OnlyContain(a => !string.IsNullOrEmpty(a));
    }

    [Fact]
    public async Task Connecting_an_account_is_refused_when_the_app_has_no_client_credentials()
    {
        CapturingHttpHandler handler = new();
        GitHubOptions options = new() { AppId = "1", PrivateKeyPem = TestPrivateKey(), WebhookSecret = "s" };
        GitHubAppClient client = new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(options), NullLogger<GitHubAppClient>.Instance);

        client.IsUserOAuthConfigured.Should().BeFalse();

        GitHubApiException error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.ExchangeUserCodeAsync("code", "verifier", CancellationToken.None));

        error.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        handler.Requests.Should().BeEmpty("nothing may be sent to GitHub by an unconfigured deployment");
    }

    [Fact]
    public async Task A_branch_name_with_slashes_is_percent_encoded_per_segment()
    {
        (GitHubAppClient client, CapturingHttpHandler handler) = New();
        handler.Responder = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)
            ? handler.Json("{\"token\":\"ghs\",\"expires_at\":\"2099-01-01T00:00:00Z\"}")
            : handler.Json("{\"object\":{\"sha\":\"commit-sha\"}}");

        string head = await client.GetBranchHeadAsync(11, "acme", "promo-video", "feature/video", CancellationToken.None);

        head.Should().Be("commit-sha");
        handler.Requests.Single(r => r.Url.Contains("/git/ref/", StringComparison.Ordinal)).Url
            .Should().Contain("/git/ref/heads/feature/video");
    }
}
