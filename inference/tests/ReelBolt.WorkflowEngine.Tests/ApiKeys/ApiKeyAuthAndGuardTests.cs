using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Controllers;
using InferenceRoutes = ReelBolt.Inference.Api.McpScopeRoutes;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.ApiKeys.ApiKeyTestSupport;

namespace ReelBolt.WorkflowEngine.Tests.ApiKeys;

public class ApiKeyHandlerTests
{
    private sealed class Rig : IDisposable
    {
        public required ServiceProvider Root { get; init; }
        public required InferenceApiDbContext Db { get; init; }
        public void Dispose() => Root.Dispose();
    }

    private static Rig NewRig(bool enabled = true)
    {
        InferenceApiDbContext db = NewDb();
        ServiceCollection s = new();
        s.AddLogging();
        s.AddMemoryCache();
        s.AddSingleton(db);
        s.AddSingleton<InferenceApiDbContext>(db);
        s.AddSingleton<MembershipValidator>();
        s.Configure<PublicApiOptions>(o => o.Enabled = enabled);
        s.AddScoped<IApiKeyService>(_ => Service(db));
        s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthentication.Scheme, _ => { });
        return new Rig { Root = s.BuildServiceProvider(), Db = db };
    }

    private static DefaultHttpContext Ctx(Rig rig, string token)
    {
        DefaultHttpContext ctx = new() { RequestServices = rig.Root.CreateScope().ServiceProvider };
        ctx.Request.Headers.Authorization = "Bearer " + token;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string Claim(ClaimsPrincipal p, string type) => p.FindFirst(type)?.Value ?? "";

    [Fact]
    public async Task Principal_is_the_service_user_as_Member_never_admin()
    {
        using Rig rig = NewRig();
        var (creator, org) = await SeedCreatorAsync(rig.Db, OrganizationRole.Owner, platformAdmin: true);
        CreatedApiKey key = await Service(rig.Db).CreateAsync(Command(org.Id, creator.Id, null, ["runs.read", "files.read"]), default);

        AuthenticateResult r = await Ctx(rig, key.Token).AuthenticateAsync(ApiKeyAuthentication.Scheme);
        r.Succeeded.Should().BeTrue();
        ClaimsPrincipal p = r.Principal!;
        Claim(p, "sub").Should().Be(key.Key.ServiceUserId.ToString()).And.NotBe(creator.Id.ToString());
        Claim(p, AuthClaims.Organization).Should().Be(org.Id.ToString());
        Claim(p, AuthClaims.OrgRole).Should().Be("Member");
        Claim(p, AuthClaims.PlatformAdmin).Should().Be("false");
        Claim(p, AuthClaims.IsAdminAlias).Should().Be("false");
        Claim(p, AuthClaims.Scope).Should().Be("runs.read files.read");
        Claim(p, AuthClaims.ApiKeyId).Should().Be(key.Key.Id.ToString());
        Claim(p, AuthClaims.EmailVerified).Should().Be("true");
    }

    [Fact]
    public async Task Creator_leaving_does_not_break_the_key()
    {
        using Rig rig = NewRig();
        var (creator, org) = await SeedCreatorAsync(rig.Db);
        CreatedApiKey key = await Service(rig.Db).CreateAsync(Command(org.Id, creator.Id), default);
        rig.Db.OrganizationMemberships.Remove(rig.Db.OrganizationMemberships.Single(m => m.UserId == creator.Id));
        rig.Db.ApplicationUsers.Remove(creator);
        await rig.Db.SaveChangesAsync();

        (await Ctx(rig, key.Token).AuthenticateAsync(ApiKeyAuthentication.Scheme)).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(OrganizationRole.Admin)]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Billing)]
    public async Task Service_user_with_a_non_Member_role_is_refused(OrganizationRole role)
    {
        using Rig rig = NewRig();
        var (creator, org) = await SeedCreatorAsync(rig.Db);
        CreatedApiKey key = await Service(rig.Db).CreateAsync(Command(org.Id, creator.Id), default);
        rig.Db.OrganizationMemberships.Single(m => m.UserId == key.Key.ServiceUserId).Role = role;
        await rig.Db.SaveChangesAsync();

        DefaultHttpContext ctx = Ctx(rig, key.Token);
        (await ctx.AuthenticateAsync(ApiKeyAuthentication.Scheme)).Succeeded.Should().BeFalse();
        ctx.Items[ApiKeyAuthentication.FailureItem].Should().Be("api_key_inactive");
    }

    [Fact]
    public async Task Missing_membership_or_non_service_user_is_refused()
    {
        using Rig rig = NewRig();
        var (creator, org) = await SeedCreatorAsync(rig.Db);
        CreatedApiKey a = await Service(rig.Db).CreateAsync(Command(org.Id, creator.Id), default);
        CreatedApiKey b = await Service(rig.Db).CreateAsync(Command(org.Id, creator.Id), default);
        rig.Db.OrganizationMemberships.Remove(rig.Db.OrganizationMemberships.Single(m => m.UserId == a.Key.ServiceUserId));
        rig.Db.ApplicationUsers.Single(u => u.Id == b.Key.ServiceUserId).IsServiceAccount = false;
        await rig.Db.SaveChangesAsync();

        (await Ctx(rig, a.Token).AuthenticateAsync(ApiKeyAuthentication.Scheme)).Succeeded.Should().BeFalse();
        (await Ctx(rig, b.Token).AuthenticateAsync(ApiKeyAuthentication.Scheme)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_key_of_a_soft_deleted_organization_is_refused_as_inactive()
    {
        // M7/M8: the org delete is soft, so the key and service user outlive it until the purge.
        using Rig rig = NewRig();
        var (creator, org) = await SeedCreatorAsync(rig.Db);
        CreatedApiKey key = await Service(rig.Db).CreateAsync(Command(org.Id, creator.Id), default);
        (await Ctx(rig, key.Token).AuthenticateAsync(ApiKeyAuthentication.Scheme)).Succeeded.Should().BeTrue();

        rig.Db.Organizations.Single(o => o.Id == org.Id).DeletedAt = DateTime.UtcNow;
        await rig.Db.SaveChangesAsync();

        DefaultHttpContext ctx = Ctx(rig, key.Token);
        (await ctx.AuthenticateAsync(ApiKeyAuthentication.Scheme)).Succeeded.Should().BeFalse();
        ctx.Items[ApiKeyAuthentication.FailureItem].Should().Be("api_key_inactive");
    }

    [Fact]
    public async Task Revoked_flag_off_and_unknown_fail_with_a_401_json_challenge()
    {
        using Rig rig = NewRig();
        var (creator, org) = await SeedCreatorAsync(rig.Db);
        CreatedApiKey key = await Service(rig.Db).CreateAsync(Command(org.Id, creator.Id), default);
        rig.Db.ApiKeys.Single().RevokedAt = DateTime.UtcNow;
        await rig.Db.SaveChangesAsync();

        async Task<(int, string)> Challenge(Rig r, string token)
        {
            DefaultHttpContext ctx = Ctx(r, token);
            await ctx.AuthenticateAsync(ApiKeyAuthentication.Scheme);
            await ctx.ChallengeAsync(ApiKeyAuthentication.Scheme);
            ctx.Response.Body.Position = 0;
            using JsonDocument doc = await JsonDocument.ParseAsync(ctx.Response.Body);
            return (ctx.Response.StatusCode, doc.RootElement.GetProperty("error").GetProperty("code").GetString()!);
        }

        (await Challenge(rig, key.Token)).Should().Be((401, "api_key_revoked"));
        (await Challenge(rig, ApiKeyToken.Generate().Token)).Should().Be((401, "invalid_api_key"));
        using Rig off = NewRig(enabled: false);
        (await Challenge(off, key.Token)).Should().Be((401, "api_disabled"));
    }

    [Fact]
    public void Only_rbk_bearer_requests_go_to_the_key_scheme()
    {
        DefaultHttpContext Req(string? h) { DefaultHttpContext c = new(); if (h is not null) c.Request.Headers.Authorization = h; return c; }
        ApiKeyAuthentication.IsApiKeyRequest(Req("Bearer rbk_x").Request).Should().BeTrue();
        ApiKeyAuthentication.IsApiKeyRequest(Req("Bearer eyJhbGciOi").Request).Should().BeFalse();
        ApiKeyAuthentication.IsApiKeyRequest(Req(null).Request).Should().BeFalse();
    }
}

public class ApiKeyScopeGuardTests
{
    private static ClaimsPrincipal KeyPrincipal(string scope) => new(new ClaimsIdentity(
        [new Claim("sub", "u"), new Claim(AuthClaims.Scope, scope), new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString())], "ApiKey"));

    private static async Task<(int Status, bool Next, string Body)> Run(ClaimsPrincipal user, string method, string path, params string[] requireScopes)
    {
        bool next = false;
        McpScopeGuardMiddleware mw = new(_ => { next = true; return Task.CompletedTask; }, InferenceRoutes.Allowed);
        DefaultHttpContext ctx = new() { User = user };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (requireScopes.Length > 0)
            ctx.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new RequireScopeAttribute(requireScopes)), "t"));
        else
            ctx.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(), "t"));
        await mw.InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, next, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Key_with_every_scope_is_refused_on_an_endpoint_without_RequireScope()
    {
        (int status, bool next, string body) = await Run(KeyPrincipal(string.Join(' ', ApiScopes.All)), "GET", "/api/v1/projects");
        status.Should().Be(403);
        next.Should().BeFalse();
        body.Should().Contain("api_key_scope");
    }

    [Fact]
    public async Task RequireScope_admits_a_holder_and_refuses_the_rest_with_required_scopes()
    {
        (await Run(KeyPrincipal("runs.read"), "GET", "/x", "runs.read", "runs.execute")).Next.Should().BeTrue();
        (int status, bool next, string body) = await Run(KeyPrincipal("files.read"), "GET", "/x", "runs.read", "runs.execute");
        (status, next).Should().Be((403, false));
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement err = doc.RootElement.GetProperty("error");
        err.GetProperty("code").GetString().Should().Be("insufficient_scope");
        err.GetProperty("requiredScopes").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("runs.read", "runs.execute");
    }

    [Fact]
    public async Task Mcp_allowlist_and_error_code_are_unchanged()
    {
        ClaimsPrincipal mcp = new(new ClaimsIdentity([new Claim("sub", "u"), new Claim(AuthClaims.Scope, "assistant.tools")], "t"));
        (await Run(mcp, "GET", InferenceRoutes.ToolCatalog)).Next.Should().BeTrue();
        (int status, _, string body) = await Run(mcp, "GET", "/api/v1/projects");
        status.Should().Be(403);
        body.Should().Contain("mcp_token_scope");
    }

    [Fact]
    public async Task Unscoped_session_principal_is_untouched()
    {
        ClaimsPrincipal session = new(new ClaimsIdentity([new Claim("sub", "u")], "t"));
        (await Run(session, "GET", "/api/v1/projects")).Next.Should().BeTrue();
    }

    [Fact]
    public void No_dashboard_controller_declares_RequireScope()
    {
        // Every dashboard controller action is therefore refused for an API key by the guard test above. Only the
        // public v1 controllers (under /api/public/v1) declare scopes; PublicApiScopeEnforcementTests pins those.
        typeof(OrgApiKeysController).Assembly.GetTypes()
            .Where(t => !typeof(ReelBolt.Inference.Api.Controllers.PublicApi.PublicApiControllerBase).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes<RequireScopeAttribute>(true).Any()
                || t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(m => m.GetCustomAttributes<RequireScopeAttribute>(true).Any()))
            .Should().BeEmpty("no dashboard route may admit a key; only the public v1 controllers declare scopes");
    }

    [Fact]
    public void Guard_sits_after_authentication_with_implicit_routing_before_it()
    {
        string program = File.ReadAllText(FindUp("inference/src/ReelBolt.Inference.Api/Program.cs"));
        program.Should().NotContain("UseRouting", "WebApplication inserts routing first, so GetEndpoint() is set when the guard runs");
        program.IndexOf("UseAuthentication", StringComparison.Ordinal).Should().BeLessThan(program.IndexOf("UseMcpScopeGuard", StringComparison.Ordinal));
        program.IndexOf("UseMcpScopeGuard", StringComparison.Ordinal).Should().BeLessThan(program.IndexOf("UseAuthorization", StringComparison.Ordinal));
    }

    private static string FindUp(string rel)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, rel))) dir = Path.GetDirectoryName(dir);
        return dir is null ? throw new FileNotFoundException(rel) : Path.Combine(dir, rel);
    }
}
