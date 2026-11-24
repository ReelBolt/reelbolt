#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing.Links;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing.Links;

/// <summary>
/// The tracked-link routes through the real ASP.NET Core pipeline (routing, authentication,
/// authorization, model binding and JSON), as <see cref="MarketingConsentControllerTests"/> does for the
/// consent routes: what a browser, a link checker and the admin page actually get on the wire.
/// </summary>
public sealed class TrackedLinkPipelineTests : IDisposable
{
    private const string Browser =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private const string LongPath =
        "/for/software-teams?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&for=software";

    private readonly MarketingHarness _h = new();
    private readonly Guid _admin;

    public TrackedLinkPipelineTests()
    {
        _admin = _h.AddUser("admin@example.test", _h.Now);
    }

    public void Dispose() => _h.Dispose();

    /// <summary>Only the two tracked-link controllers are discovered, so the host needs nothing the rest of the API needs.</summary>
    private sealed class OnlyLinkControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(TrackedLinkRedirectController) || typeInfo.AsType() == typeof(MarketingLinkAdminController);
    }

    /// <summary>Signs a request in when it carries <c>X-Test-User</c>: <c>admin</c> is a platform admin, anything else is not.</summary>
    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string user = Request.Headers["X-Test-User"].ToString();
            if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            ClaimsIdentity identity = new([new Claim("role", user)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class HeaderUser(IHttpContextAccessor http, Guid adminId) : ICurrentUser
    {
        public Guid UserId => adminId;
        public string Email => "admin@example.test";
        public bool IsAuthenticated => http.HttpContext?.User.Identity?.IsAuthenticated == true;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool IsPlatformAdmin => http.HttpContext?.User.FindFirst("role")?.Value == "admin";
    }

    private sealed class FakeFunnel : ITrackedLinkFunnelReader
    {
        public Task<TrackedLinkFunnel> ReadAsync(DateOnly sinceDay, CancellationToken ct)
            => Task.FromResult(new TrackedLinkFunnel([], new FunnelAccountSteps(0, 0, 0, 0, 0, 0, 0)));
    }

    private async Task<(WebApplication App, HttpClient Client)> StartHostAsync(bool linksEnabled = true)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m =>
        {
            m.ApplicationParts.Clear();
            m.ApplicationParts.Add(new AssemblyPart(typeof(TrackedLinkRedirectController).Assembly));
            m.FeatureProviders.Clear();
            m.FeatureProviders.Add(new OnlyLinkControllers());
        });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<TimeProvider>(_h.Clock);
        builder.Services.AddScoped(_ => _h.NewDb());
        builder.Services.AddScoped<TrackedLinkService>();
        builder.Services.AddScoped<ITrackedLinkDirectory>(sp => sp.GetRequiredService<TrackedLinkService>());
        builder.Services.AddSingleton<ITrackedLinkFunnelReader, FakeFunnel>();
        builder.Services.AddScoped<ICurrentUser>(sp => new HeaderUser(sp.GetRequiredService<IHttpContextAccessor>(), _admin));
        builder.Services.Configure<MarketingLinksOptions>(o => o.Enabled = linksEnabled);
        builder.Services.Configure<AttributionOptions>(_ => { });

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        // A browser follows a redirect; this client must not, so the 302 itself can be read.
        HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) };
        return (app, client);
    }

    private void AddLink(Action<MarketingLink>? change = null)
    {
        using InferenceApiDbContext db = _h.NewDb();
        MarketingLink link = new()
        {
            Id = Guid.NewGuid(),
            Code = "hn-launch",
            Channel = "hackernews",
            Medium = "community",
            Campaign = "launch-2026-11",
            Placement = "Show HN, first comment",
            DestinationPath = "/for/software-teams",
            Persona = "software",
            CreatedAt = _h.Now,
        };
        change?.Invoke(link);
        db.MarketingLinks.Add(link);
        db.SaveChanges();
    }

    private (int Clicks, int BotHits) Counted()
    {
        using InferenceApiDbContext db = _h.NewDb();
        List<MarketingLinkDailyStat> rows = db.MarketingLinkDailyStats.AsNoTracking().ToList();
        return (rows.Sum(r => r.Clicks), rows.Sum(r => r.BotHits));
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? userAgent = Browser, string? user = null, object? json = null)
    {
        HttpRequestMessage request = new(method, path);
        if (userAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (user is not null) request.Headers.Add("X-Test-User", user);
        if (json is not null)
        {
            request.Content = json is string raw
                ? new StringContent(raw, Encoding.UTF8, "application/json")
                : JsonContent.Create(json);
        }

        return request;
    }

    private static void ShouldBeRedirectTo(HttpResponseMessage response, string location)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.GetValues("Location").Should().Equal(new[] { location }, "the Location header is exactly the relative path");
        response.Headers.GetValues("Cache-Control").Should().Equal("no-store");
        response.Headers.Contains("Set-Cookie").Should().BeFalse("a tracked link writes nothing to the device");
    }

    [Fact]
    public async Task A_browser_following_a_tracked_link_gets_a_relative_302_with_no_cookie_and_no_credentials_needed()
    {
        AddLink();
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/l/hn-launch"));

        ShouldBeRedirectTo(response, LongPath);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        Counted().Should().Be((1, 0));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("/api/v1/l/HN-LAUNCH")]
    [InlineData("/api/v1/l/HN-Launch")]
    public async Task A_link_typed_in_another_letter_case_is_followed_and_counted_like_the_stored_one(string path)
    {
        // The self-hosted edge forwards the path as typed; the Pages Function lowers it. Same link either way.
        AddLink();
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        ShouldBeRedirectTo(await client.SendAsync(Request(HttpMethod.Get, path)), LongPath);
        Counted().Should().Be((1, 0));
        await app.StopAsync();
    }

    [Fact]
    public async Task A_head_request_and_a_link_preview_are_redirected_and_counted_as_bot_hits()
    {
        AddLink();
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        ShouldBeRedirectTo(await client.SendAsync(Request(HttpMethod.Head, "/api/v1/l/hn-launch")), LongPath);
        ShouldBeRedirectTo(await client.SendAsync(Request(HttpMethod.Get, "/api/v1/l/hn-launch", userAgent: "Twitterbot/1.0")), LongPath);
        ShouldBeRedirectTo(await client.SendAsync(Request(HttpMethod.Get, "/api/v1/l/hn-launch", userAgent: null)), LongPath);

        Counted().Should().Be((0, 3));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("/api/v1/l/no-such-link")]
    [InlineData("/api/v1/l/HN_LAUNCH")]
    [InlineData("/api/v1/l/hn-launch/extra/segments")]
    [InlineData("/api/v1/l/")]
    [InlineData("/api/v1/l")]
    [InlineData("/api/v1/l/hn_launch")]
    [InlineData("/api/v1/l/%2F%2Fevil.example")]
    public async Task Anything_that_is_not_an_active_link_is_a_302_to_the_home_page_and_never_a_404(string path)
    {
        AddLink();
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        ShouldBeRedirectTo(await client.SendAsync(Request(HttpMethod.Get, path)), "/");
        Counted().Should().Be((0, 0));
        await app.StopAsync();
    }

    [Fact]
    public async Task An_archived_link_is_a_302_to_the_home_page()
    {
        AddLink(l => l.ArchivedAt = _h.Now);
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        ShouldBeRedirectTo(await client.SendAsync(Request(HttpMethod.Get, "/api/v1/l/hn-launch")), "/");
        Counted().Should().Be((0, 0));
        await app.StopAsync();
    }

    [Fact]
    public async Task While_the_switch_is_off_the_redirect_is_a_404()
    {
        AddLink();
        (WebApplication app, HttpClient client) = await StartHostAsync(linksEnabled: false);
        await using WebApplication _ = app;
        using HttpClient __ = client;

        HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/l/hn-launch"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Contains("Location").Should().BeFalse();
        Counted().Should().Be((0, 0));
        await app.StopAsync();
    }

    [Fact]
    public async Task Only_get_and_head_reach_the_redirect()
    {
        AddLink();
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        (await client.SendAsync(Request(HttpMethod.Post, "/api/v1/l/hn-launch"))).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        Counted().Should().Be((0, 0));
        await app.StopAsync();
    }

    [Fact]
    public async Task The_admin_routes_need_a_signed_in_platform_admin()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        foreach (string path in new[] { "/api/v1/marketing/admin/links", "/api/v1/marketing/admin/links/funnel" })
        {
            (await client.SendAsync(Request(HttpMethod.Get, path))).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized, "{0} is not anonymous", path);
            (await client.SendAsync(Request(HttpMethod.Get, path, user: "member"))).StatusCode
                .Should().Be(HttpStatusCode.Forbidden, "{0} is for platform admins", path);
            (await client.SendAsync(Request(HttpMethod.Get, path, user: "admin"))).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        object body = new { code = "x-1", channel = "x", medium = "social", campaign = "c", placement = "p", destinationPath = "/" };
        (await client.SendAsync(Request(HttpMethod.Post, "/api/v1/marketing/admin/links", json: body))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Request(HttpMethod.Post, "/api/v1/marketing/admin/links", user: "member", json: body))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Request(HttpMethod.Patch, $"/api/v1/marketing/admin/links/{Guid.NewGuid()}", user: "member", json: new { archived = true })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingLinks.Count().Should().Be(0);
        await app.StopAsync();
    }

    [Fact]
    public async Task The_admin_page_can_create_list_edit_and_read_the_funnel_with_camel_case_json()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        // Create: the POST body of the contract.
        HttpResponseMessage created = await client.SendAsync(Request(
            HttpMethod.Post, "/api/v1/marketing/admin/links", user: "admin",
            json: """{"code":"hn-launch","channel":"hackernews","medium":"community","campaign":"launch-2026-11","placement":"Show HN, first comment","destinationPath":"/for/software-teams","persona":"software","costMinor":2500,"costCurrency":"USD"}"""));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        JsonElement link = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        link.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "code", "channel", "medium", "campaign", "placement", "destinationPath", "persona",
            "costMinor", "costCurrency", "createdAt", "archivedAt", "shortPath", "longPath", "totalClicks");
        link.GetProperty("shortPath").GetString().Should().Be("/l/hn-launch");
        link.GetProperty("longPath").GetString().Should().Be(LongPath);
        link.GetProperty("archivedAt").ValueKind.Should().Be(JsonValueKind.Null);
        string id = link.GetProperty("id").GetString()!;

        // The same code again is 409 link_code_taken; a bad channel is 400 with its code.
        HttpResponseMessage duplicate = await client.SendAsync(Request(
            HttpMethod.Post, "/api/v1/marketing/admin/links", user: "admin",
            json: new { code = "hn-launch", channel = "reddit", medium = "social", campaign = "c", placement = "p", destinationPath = "/" }));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadAsStringAsync()).Should().Be("""{"error":"link_code_taken","field":"code"}""");
        HttpResponseMessage invalid = await client.SendAsync(Request(
            HttpMethod.Post, "/api/v1/marketing/admin/links", user: "admin",
            json: new { code = "tw-1", channel = "twitter", medium = "social", campaign = "c", placement = "p", destinationPath = "/" }));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).Should().Be("""{"error":"channel_invalid"}""");

        // One click, then the list shows it.
        (await client.SendAsync(Request(HttpMethod.Get, "/api/v1/l/hn-launch"))).StatusCode.Should().Be(HttpStatusCode.Found);
        JsonElement list = JsonDocument.Parse(await (await client.SendAsync(
            Request(HttpMethod.Get, "/api/v1/marketing/admin/links", user: "admin"))).Content.ReadAsStringAsync()).RootElement;
        JsonElement listed = list.GetProperty("items").EnumerateArray().Single();
        listed.GetProperty("code").GetString().Should().Be("hn-launch");
        listed.GetProperty("totalClicks").GetInt64().Should().Be(1);

        // Edit: an explicit null clears, an absent field stays, an immutable field is refused.
        HttpResponseMessage patched = await client.SendAsync(Request(
            HttpMethod.Patch, $"/api/v1/marketing/admin/links/{id}", user: "admin",
            json: """{"persona":null,"placement":"Show HN, top comment","archived":true}"""));
        patched.StatusCode.Should().Be(HttpStatusCode.OK, await patched.Content.ReadAsStringAsync());
        JsonElement edited = JsonDocument.Parse(await patched.Content.ReadAsStringAsync()).RootElement;
        edited.GetProperty("persona").ValueKind.Should().Be(JsonValueKind.Null);
        edited.GetProperty("placement").GetString().Should().Be("Show HN, top comment");
        edited.GetProperty("costMinor").GetInt64().Should().Be(2500, "a field the edit does not name is left alone");
        edited.GetProperty("archivedAt").ValueKind.Should().Be(JsonValueKind.String);
        edited.GetProperty("longPath").GetString().Should().NotContain("&for=");
        edited.GetProperty("totalClicks").GetInt64().Should().Be(1);

        HttpResponseMessage immutable = await client.SendAsync(Request(
            HttpMethod.Patch, $"/api/v1/marketing/admin/links/{id}", user: "admin", json: """{"channel":"reddit"}"""));
        immutable.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await immutable.Content.ReadAsStringAsync()).Should().Be("""{"error":"field_immutable","field":"channel"}""");

        (await client.SendAsync(Request(
            HttpMethod.Patch, $"/api/v1/marketing/admin/links/{Guid.NewGuid()}", user: "admin", json: """{"archived":true}"""))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await client.SendAsync(Request(
            HttpMethod.Patch, "/api/v1/marketing/admin/links/not-a-guid", user: "admin", json: """{"archived":true}"""))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the id is a GUID route value");

        // The archived link no longer redirects to its landing page and counts nothing more.
        ShouldBeRedirectTo(await client.SendAsync(Request(HttpMethod.Get, "/api/v1/l/hn-launch")), "/");
        Counted().Should().Be((1, 0));

        // Funnel: days from the query string, 30 without one.
        JsonElement funnel = JsonDocument.Parse(await (await client.SendAsync(
            Request(HttpMethod.Get, "/api/v1/marketing/admin/links/funnel?days=7", user: "admin"))).Content.ReadAsStringAsync()).RootElement;
        funnel.EnumerateObject().Select(p => p.Name).Should().Equal(
            "days", "generatedAt", "urlCaptureEnabled", "visitIngestEnabled", "items", "unattributed");
        funnel.GetProperty("days").GetInt32().Should().Be(7);
        funnel.GetProperty("urlCaptureEnabled").GetBoolean().Should().BeFalse();
        funnel.GetProperty("visitIngestEnabled").GetBoolean().Should().BeFalse("the visit beacon is off unless the operator turns it on");
        funnel.GetProperty("unattributed").EnumerateObject().Select(p => p.Name).Should().Equal(
            "signups", "verified", "onboarded", "firstProject", "firstRun", "firstVideo", "paid");
        JsonDocument.Parse(await (await client.SendAsync(
                Request(HttpMethod.Get, "/api/v1/marketing/admin/links/funnel", user: "admin"))).Content.ReadAsStringAsync())
            .RootElement.GetProperty("days").GetInt32().Should().Be(30);
        await app.StopAsync();
    }
}
