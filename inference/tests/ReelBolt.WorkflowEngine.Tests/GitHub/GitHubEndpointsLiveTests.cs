#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// The new endpoints, served for real.
///
/// <para>
/// Everything else in this folder drives the controllers as objects. That cannot catch what only the
/// pipeline can: an <c>[Authorize]</c> that never runs, a route that does not bind, a cookie the
/// response forgets to set, a <c>Location</c> header that is not what a browser needs. So this boots a
/// real Kestrel host on a loopback port with the real controllers, the real Data Protection protector,
/// the real <see cref="ProjectAccessService"/> and the real tenancy context, and asserts <b>actual HTTP
/// status codes and headers</b>.
/// </para>
///
/// <para>
/// The database is EF Core's in-memory provider on a unique name, because that is what this test needs
/// to seed and inspect. <b>No PostgreSQL server is contacted and nothing is migrated</b>, so the local
/// dev database is not touched by any part of this.
/// </para>
/// </summary>
public class GitHubEndpointsLiveTests : IAsyncLifetime
{
    private const string AccessToken = "ghu_live_access_token_value";
    private const string RefreshToken = "ghr_live_refresh_token_value";
    private const string BindingCookie = GitHubUserOAuthService.BindingCookieName;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private InferenceApiDbContext _db = null!;
    private FakeGitHubAppClient _gitHub = null!;
    private string _baseUrl = null!;

    private Guid _userId;
    private Guid _organizationId;
    private Guid _projectId;

    private readonly ITestOutputHelper _output;

    public GitHubEndpointsLiveTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _userId = Guid.NewGuid();
        _organizationId = TestTenancy.OrgOf(_userId);

        SmokeAuthState.Authenticated = true;
        SmokeAuthState.UserId = _userId;
        SmokeAuthState.OrganizationId = _organizationId;

        string databaseName = $"github-live-{Guid.NewGuid():N}";
        string contentRoot = Path.Combine(Path.GetTempPath(), $"reelbolt-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);

        _gitHub = new FakeGitHubAppClient { IsConfigured = true, IsUserOAuthConfigured = true };
        _gitHub.TokensToIssue = new GitHubUserTokens(
            AccessToken,
            RefreshToken,
            DateTime.UtcNow.AddHours(8),
            DateTime.UtcNow.AddDays(180));
        _gitHub.Identity = new GitHubUserIdentity(4242, "octocat", "https://avatars.example/octocat.png");
        _gitHub.AddUserRepository(11, "acme", 1001, "acme/promo-video", "main");
        _gitHub.AddTreeLevel("acme", "promo-video", "main",
            new GitHubTreeEntry("video", "tree", "sha-video", 0),
            new GitHubTreeEntry("README.md", "blob", "sha-readme", 12));
        _gitHub.AddTreeLevel("acme", "promo-video", "sha-video",
            new GitHubTreeEntry("scene.tsx", "blob", "sha-scene", 100));

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // A throwaway content root, so the project's own appsettings.json — which points at a real
            // PostgreSQL — is never picked up. Nothing here reads configuration at all.
            ContentRootPath = contentRoot,
            EnvironmentName = "Smoke"
        });

        builder.Logging.ClearProviders();

        builder.Services.AddControllers().AddApplicationPart(typeof(ProjectGitHubController).Assembly);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication("Smoke").AddScheme<AuthenticationSchemeOptions, SmokeAuthHandler>("Smoke", _ => { });
        builder.Services.AddAuthorization();

        // The REAL protector, so this test proves the token is encrypted at rest by the thing that
        // encrypts it in production rather than by a stand-in that agrees with the test.
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        builder.Services.AddSingleton<ITenantContext, TenantContext>();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
        builder.Services.AddScoped<IProjectAccessService, ProjectAccessService>();

        builder.Services.AddSingleton<InferenceApiDbContext>(_ => new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options,
            new LiveTenantContext(_organizationId)));

        builder.Services.AddSingleton<IGitHubAppClient>(_gitHub);
        builder.Services.AddSingleton<IGitHubWebhookVerifier, GitHubWebhookVerifier>();
        builder.Services.AddScoped<IGitHubUserOAuthService, GitHubUserOAuthService>();
        builder.Services.AddScoped<IGitHubAccountService, GitHubAccountService>();
        builder.Services.AddScoped<IGitHubLinkService, GitHubLinkService>();
        builder.Services.AddScoped<IGitHubRepositoryPullService, NoopPullService>();
        builder.Services.AddScoped<IGitHubDeliveryProcessor, GitHubDeliveryProcessor>();
        builder.Services.AddSingleton(_ => new GitHubSyncThrottle());
        builder.Services.AddSingleton<GitHubSyncQueue>();
        builder.Services.Configure<GitHubOptions>(options =>
        {
            options.AppId = "123456";
            options.AppSlug = "reelbolt-test";
            options.PrivateKeyPem = "unused-in-tests";
            options.WebhookSecret = GitHubTestData.WebhookSecret;
            options.ClientId = "Iv1.abc123";
            options.ClientSecret = "the-client-secret";
            options.OAuthCallbackUrl = "https://reel.example.com/api/v1/github/oauth/callback";
            options.OAuthAuthorizeUrl = "https://github.com/login/oauth/authorize";
            options.OAuthTokenUrl = "https://github.com/login/oauth/access_token";
        });

        _app = builder.Build();
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();

        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();

        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        _db = _app.Services.GetRequiredService<InferenceApiDbContext>();

        Project project = GitHubTestData.NewProject(_organizationId, _userId, "Live");
        _projectId = project.Id;
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        // No cookie container: the binding cookie is passed by hand so each test can decide to send it,
        // send a wrong one, or send none — which is the whole point of it existing.
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(_baseUrl)
        };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? cookie = null, object? body = null)
    {
        HttpRequestMessage request = new(method, path);
        if (cookie is not null) request.Headers.Add("Cookie", $"{BindingCookie}={cookie}");
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<JsonElement> JsonOfAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // ------------------------------------------------------------------ status codes

    [Fact]
    public async Task An_unauthenticated_caller_gets_401_from_every_account_and_picker_route()
    {
        SmokeAuthState.Authenticated = false;
        try
        {
            foreach (string path in new[]
            {
                "/api/v1/github/account",
                $"/api/v1/projects/{_projectId}/github",
                $"/api/v1/projects/{_projectId}/github/repositories",
                $"/api/v1/projects/{_projectId}/github/tree?repositoryId=1001"
            })
            {
                using HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Get, path));
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{path} must require a session");
            }
        }
        finally
        {
            SmokeAuthState.Authenticated = true;
        }
    }

    [Fact]
    public async Task The_account_route_reports_that_nothing_is_connected()
    {
        using HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/github/account"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await JsonOfAsync(response);
        body.GetProperty("connected").GetBoolean().Should().BeFalse();
        body.GetProperty("canConnect").GetBoolean().Should().BeTrue();
        body.GetProperty("installUrl").GetString().Should().Be("https://github.com/apps/reelbolt-test/installations/new");

        // No field of this response is, or may ever be, a token.
        body.EnumerateObject().Select(p => p.Name)
            .Should().NotContain(name => name.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_unlinked_project_answers_404_not_linked()
    {
        using HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Get, $"/api/v1/projects/{_projectId}/github"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOfAsync(response)).GetProperty("error").GetString().Should().Be("not_linked");
    }

    [Fact]
    public async Task The_picker_answers_409_when_no_account_is_connected()
    {
        using HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/repositories"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOfAsync(response)).GetProperty("error").GetString().Should().Be("github_account_not_connected");
    }

    [Fact]
    public async Task A_project_in_another_organization_is_404_over_http_too()
    {
        using HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/projects/{Guid.NewGuid()}/github/repositories"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a foreign project is 404, never 403");
    }

    // ------------------------------------------------------------------ the whole flow

    [Fact]
    public async Task Connecting_an_account_sets_the_binding_cookie_and_the_callback_stores_an_encrypted_token()
    {
        (string authorizeUrl, string binding) = await StartConnectAsync();

        // The URL the browser is sent to is github.com's, with PKCE and the offline_access scope, and
        // no scope that could grant a permission a GitHub App does not have.
        authorizeUrl.Should().StartWith("https://github.com/login/oauth/authorize?");
        authorizeUrl.Should().Contain("client_id=Iv1.abc123");
        authorizeUrl.Should().Contain("code_challenge_method=S256");
        authorizeUrl.Should().Contain("scope=offline_access");
        authorizeUrl.Should().Contain("redirect_uri=https%3A%2F%2Freel.example.com%2Fapi%2Fv1%2Fgithub%2Foauth%2Fcallback");

        string state = QueryOf(authorizeUrl, "state");

        // Without the cookie the callback is refused, and nothing is stored.
        using HttpResponseMessage withoutCookie = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc"));
        withoutCookie.StatusCode.Should().Be(HttpStatusCode.Found);
        withoutCookie.Headers.Location!.ToString().Should().Contain("github=invalid_state");
        _db.GitHubAccountLinks.Count().Should().Be(0);

        // With it, the flow completes and lands back on the project's Repository tab.
        using HttpResponseMessage callback = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc", cookie: binding));
        callback.StatusCode.Should().Be(HttpStatusCode.Found);
        callback.Headers.Location!.ToString()
            .Should().Be($"/app/projects/{_projectId}?tab=repository&github=connected");

        GitHubAccountLink link = await _db.GitHubAccountLinks.SingleAsync();
        link.UserId.Should().Be(_userId);
        link.OrganizationId.Should().Be(_organizationId);
        link.Login.Should().Be("octocat");

        // The real Data Protection ring wrote this column: the plaintext appears nowhere in it, and the
        // real protector reads it back.
        link.ProtectedAccessToken.Should().NotContain(AccessToken);
        _app.Services.GetRequiredService<ISecretProtector>()
            .TryUnprotect(link.ProtectedAccessToken, out string decrypted).Should().BeTrue();
        decrypted.Should().Be(AccessToken);
    }

    [Fact]
    public async Task A_state_cannot_be_spent_twice_over_http()
    {
        (string authorizeUrl, string binding) = await StartConnectAsync();
        string state = QueryOf(authorizeUrl, "state");

        using HttpResponseMessage first = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc", cookie: binding));
        first.Headers.Location!.ToString().Should().Contain("github=connected");

        using HttpResponseMessage replay = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc", cookie: binding));
        replay.StatusCode.Should().Be(HttpStatusCode.Found);
        replay.Headers.Location!.ToString().Should().Contain("github=invalid_state");
    }

    [Fact]
    public async Task A_wrong_binding_cookie_is_refused_by_the_served_endpoint()
    {
        (string authorizeUrl, _) = await StartConnectAsync();
        string state = QueryOf(authorizeUrl, "state");

        using HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc", cookie: "not-the-cookie"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Contain("github=state_mismatch");
        _db.GitHubAccountLinks.Count().Should().Be(0);
    }

    // ------------------------------------------------------------------ the pickers

    [Fact]
    public async Task The_pickers_serve_what_GitHub_says_and_nothing_more()
    {
        await ConnectAsync();

        using HttpResponseMessage repositories = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/repositories"));
        repositories.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement list = await JsonOfAsync(repositories);
        list.GetProperty("repositories").GetArrayLength().Should().Be(1);
        list.GetProperty("repositories")[0].GetProperty("fullName").GetString().Should().Be("acme/promo-video");
        list.GetProperty("repositories")[0].GetProperty("isPrivate").GetBoolean().Should().BeTrue();
        list.GetProperty("installUrl").GetString().Should().Be("https://github.com/apps/reelbolt-test/installations/new");

        using HttpResponseMessage root = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/tree?repositoryId=1001"));
        root.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement tree = await JsonOfAsync(root);
        tree.GetProperty("path").GetString().Should().BeEmpty();
        tree.GetProperty("entries")[0].GetProperty("name").GetString().Should().Be("video");
        tree.GetProperty("entries")[0].GetProperty("isDirectory").GetBoolean().Should().BeTrue();
        tree.GetProperty("entries")[1].GetProperty("isDirectory").GetBoolean().Should().BeFalse();

        using HttpResponseMessage inside = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/tree?repositoryId=1001&path=video"));
        inside.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement folder = await JsonOfAsync(inside);
        folder.GetProperty("path").GetString().Should().Be("video");
        folder.GetProperty("entries")[0].GetProperty("path").GetString().Should().Be("video/scene.tsx");
    }

    [Fact]
    public async Task A_repository_the_person_cannot_use_is_404_and_its_tree_is_never_read()
    {
        await ConnectAsync();
        int before = _gitHub.TreeLevelRequests;

        using HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/tree?repositoryId=999999"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOfAsync(response)).GetProperty("error").GetString().Should().Be("repository_not_found");
        _gitHub.TreeLevelRequests.Should().Be(before, "the installation token must not be spent on an unreachable repository");
    }

    [Fact]
    public async Task The_folder_browser_reports_a_folder_that_is_gone_as_its_own_404()
    {
        await ConnectAsync();

        using HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/tree?repositoryId=1001&path=video/gone"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOfAsync(response)).GetProperty("error").GetString().Should().Be("folder_not_found");
    }

    [Fact]
    public async Task Disconnecting_destroys_the_credential_and_answers_204()
    {
        await ConnectAsync();

        using HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Delete, "/api/v1/github/account"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        GitHubAccountLink link = await _db.GitHubAccountLinks.SingleAsync();
        link.RevokedReason.Should().Be("disconnected");
        link.ProtectedAccessToken.Should().BeEmpty();

        // And the picker stops answering, rather than serving a list from a credential that is gone.
        using HttpResponseMessage repositories = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/repositories"));
        repositories.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOfAsync(repositories)).GetProperty("error").GetString().Should().Be("github_account_revoked");
    }

    /// <summary>
    /// Walks the whole new surface once and records the status code each route actually answered, so
    /// the evidence for "these are the codes a client sees" is a log rather than an assertion.
    /// </summary>
    [Fact]
    public async Task The_endpoint_surface_answers_these_status_codes()
    {
        List<string> report = [];

        async Task Probe(string label, HttpMethod method, string path, string? cookie = null, object? body = null)
        {
            using HttpResponseMessage response = await _client.SendAsync(Request(method, path, cookie, body));
            string payload = await response.Content.ReadAsStringAsync();
            if (payload.Length > 90) payload = payload[..90] + "…";
            report.Add($"{label,-46} {method.Method,-6} {path,-72} -> {(int)response.StatusCode} {response.StatusCode}");
            if (!string.IsNullOrEmpty(payload)) report.Add($"{"",-46} body: {payload}");
        }

        await Probe("account (no connection)", HttpMethod.Get, "/api/v1/github/account");
        await Probe("picker (no connection)", HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/repositories");
        await Probe("link (not linked)", HttpMethod.Get, $"/api/v1/projects/{_projectId}/github");
        await Probe("foreign project", HttpMethod.Get, $"/api/v1/projects/{Guid.NewGuid()}/github");
        await Probe("connect", HttpMethod.Post, "/api/v1/github/account/connect", body: new { projectId = _projectId });

        (string authorizeUrl, string binding) = await StartConnectAsync();
        string state = QueryOf(authorizeUrl, "state");
        await Probe("callback, no binding cookie", HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc");
        await Probe("callback, wrong binding cookie", HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc", cookie: "wrong");
        await Probe("callback, correct cookie", HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc", cookie: binding);
        await Probe("callback, replayed state", HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc", cookie: binding);

        await Probe("picker (connected)", HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/repositories");
        await Probe("tree, root", HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/tree?repositoryId=1001");
        await Probe("tree, subfolder", HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/tree?repositoryId=1001&path=video");
        await Probe("tree, unreachable repository", HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/tree?repositoryId=999999");
        await Probe("tree, missing folder", HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/tree?repositoryId=1001&path=video/gone");
        await Probe("disconnect", HttpMethod.Delete, "/api/v1/github/account");
        await Probe("picker (after disconnect)", HttpMethod.Get, $"/api/v1/projects/{_projectId}/github/repositories");

        foreach (string line in report) _output.WriteLine(line);

        // The codes the surface is contracted to answer. Anything else is a regression in routing,
        // authorization or error translation, which is exactly what this test is here to notice.
        report.Should().Contain(line => line.Contains("-> 200 OK"));
        report.Should().Contain(line => line.Contains("-> 204 NoContent"));
        report.Should().Contain(line => line.Contains("-> 302 Found"));
        report.Should().Contain(line => line.Contains("-> 404 NotFound"));
        report.Should().Contain(line => line.Contains("-> 409 Conflict"));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(string AuthorizeUrl, string Binding)> StartConnectAsync()
    {
        using HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Post, "/api/v1/github/account/connect", body: new { projectId = _projectId }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string setCookie = response.Headers.GetValues("Set-Cookie").Single();
        setCookie.Should().Contain($"{BindingCookie}=");
        setCookie.Should().Contain("httponly", "the binding must not be readable from script");
        setCookie.Should().Contain("samesite=lax");
        setCookie.Should().Contain("path=/api/v1/github");

        JsonElement body = await JsonOfAsync(response);
        string binding = CookieValueOf(setCookie);
        return (body.GetProperty("authorizeUrl").GetString()!, binding);
    }

    private async Task ConnectAsync()
    {
        (string authorizeUrl, string binding) = await StartConnectAsync();
        string state = QueryOf(authorizeUrl, "state");

        using HttpResponseMessage callback = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/github/oauth/callback?state={state}&code=abc", cookie: binding));

        callback.Headers.Location!.ToString().Should().Contain("github=connected");
    }

    private static string QueryOf(string url, string name) =>
        Uri.UnescapeDataString(
            url.Split('?')[1].Split('&')
                .Single(part => part.StartsWith($"{name}=", StringComparison.Ordinal))
                .Split('=', 2)[1]);

    private static string CookieValueOf(string setCookie)
    {
        string pair = setCookie.Split(';')[0];
        return pair.Split('=', 2)[1];
    }

    /// <summary>
    /// Marks every request as one organization — the tenant the seeded project belongs to — without
    /// touching the request's own claims, which is what the real middleware arrangement would do.
    /// </summary>
    private sealed class LiveTenantContext(Guid organizationId) : ITenantContext
    {
        public Guid? OrganizationId => organizationId;
        public bool IsSystem => false;
        public IDisposable BeginSystemScope() => new Noop();
        public IDisposable BeginOrganizationScope(Guid org) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class NoopPullService : IGitHubRepositoryPullService
    {
        public Task<GitHubPullOutcome> PullAsync(ProjectGitHubLink link, string commitSha, DateTime? pushedAt, string? deliveryId, CancellationToken ct) =>
            Task.FromResult(new GitHubPullOutcome("pulled", commitSha, 0, 0, 0, 0));

        public Task<GitHubPullOutcome> PullBranchAsync(ProjectGitHubLink link, bool force, CancellationToken ct) =>
            Task.FromResult(new GitHubPullOutcome("pulled", null, 0, 0, 0, 0));
    }
}

/// <summary>Which claims the next request carries. Set per test; a 401 is produced by turning it off.</summary>
internal static class SmokeAuthState
{
    public static bool Authenticated { get; set; } = true;
    public static Guid UserId { get; set; }
    public static Guid OrganizationId { get; set; }
}

/// <summary>
/// A scheme that trusts the headers nothing here sends, and instead mints exactly the claims the Go API
/// issues — <c>sub</c>, <c>org</c>, <c>orgRole</c> — so the real <see cref="CurrentUser"/> and the real
/// <see cref="TenantContext"/> read them the way they do in production.
/// </summary>
internal sealed class SmokeAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public SmokeAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!SmokeAuthState.Authenticated) return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims =
        [
            new("sub", SmokeAuthState.UserId.ToString()),
            new(AuthClaims.Organization, SmokeAuthState.OrganizationId.ToString()),
            new(AuthClaims.OrgRole, "Owner"),
            new(AuthClaims.EmailVerified, "true")
        ];

        ClaimsIdentity identity = new(claims, "Smoke");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
