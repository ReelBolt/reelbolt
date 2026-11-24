using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;
using Xunit;
using EngineRoutes = ReelBolt.WorkflowEngine.McpScopeRoutes;
using InferenceRoutes = ReelBolt.Inference.Api.McpScopeRoutes;

namespace ReelBolt.WorkflowEngine.Tests;

public class McpScopeGuardTests
{
    private const string Key = "unit-test-signing-key-0123456789-abcdef-0123";
    private const string Issuer = "iss";
    private const string Audience = "aud";
    private const string CalibrationPath = "/api/v1/workflow-engine/decision-calibration";

    private static readonly McpAllowedRoute[] InferenceAllowed = InferenceRoutes.Allowed;
    private static readonly McpAllowedRoute[] EngineAllowed = EngineRoutes.Allowed;

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)).Append(new Claim("sub", "u1")), "test"));

    private static ClaimsPrincipal Scoped(string scope) => Principal(("scope", scope));

    private static async Task<(int Status, bool NextCalled, string Body)> Run(
        ClaimsPrincipal? user, string method, string path, McpAllowedRoute[] allowed)
    {
        bool next = false;
        McpScopeGuardMiddleware mw = new(_ => { next = true; return Task.CompletedTask; }, allowed);
        DefaultHttpContext ctx = new();
        if (user is not null) ctx.User = user;
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        await mw.InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        string body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, next, body);
    }

    [Theory]
    [InlineData("PUT", "/api/v1/projects/p1/workflows/w1")]
    [InlineData("POST", "/api/v1/assistant/chat")]
    [InlineData("PATCH", "/api/v1/auth/settings")]
    [InlineData("GET", "/api/v1/billing/summary")]
    [InlineData("POST", "/api/v1/assistant/tool-call/extra")]
    [InlineData("GET", "/api/v1/assistant/tool-call")]
    [InlineData("POST", "/api/v1/assistant/tool-catalog")]
    // Object-effects egress consent is a UI-only act: an MCP token must never be able to grant, read or revoke it.
    [InlineData("PUT", "/api/v1/projects/p1/object-effects/consent")]
    [InlineData("DELETE", "/api/v1/projects/p1/object-effects/consent")]
    [InlineData("GET", "/api/v1/projects/p1/object-effects/consent")]
    public async Task Scoped_token_is_refused_off_the_allowlist(string method, string path)
    {
        var r = await Run(Scoped("assistant.tools assistant.admin"), method, path, InferenceAllowed);
        r.Status.Should().Be(403);
        r.NextCalled.Should().BeFalse();
        r.Body.Should().Contain("\"error\":\"mcp_token_scope\"");
    }

    [Theory]
    [InlineData("GET", "/api/v1/assistant/tool-catalog")]
    [InlineData("POST", "/api/v1/assistant/tool-call")]
    [InlineData("POST", "/api/v1/assistant/tool-call/")]
    [InlineData("post", "/API/v1/Assistant/Tool-Call")]
    public async Task Scoped_token_is_allowed_on_tool_routes(string method, string path)
    {
        foreach (string scope in new[] { "assistant.tools", "assistant.admin", "foo assistant.tools" })
        {
            var r = await Run(Scoped(scope), method, path, InferenceAllowed);
            r.NextCalled.Should().BeTrue(scope);
        }
    }

    [Fact]
    public async Task Querystring_is_ignored_because_path_excludes_it()
    {
        bool next = false;
        McpScopeGuardMiddleware mw = new(_ => { next = true; return Task.CompletedTask; }, InferenceAllowed);
        DefaultHttpContext ctx = new() { User = Scoped("assistant.tools") };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/v1/assistant/tool-catalog";
        ctx.Request.QueryString = new QueryString("?x=1");
        await mw.InvokeAsync(ctx);
        next.Should().BeTrue();
    }

    [Fact]
    public async Task Several_scope_claims_are_tolerated()
    {
        var user = Principal(("scope", "foo"), ("scope", "assistant.tools"));
        (await Run(user, "POST", "/api/v1/assistant/tool-call", InferenceAllowed)).NextCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("")]
    public async Task Unknown_or_empty_scope_is_refused_everywhere(string scope)
    {
        (await Run(Scoped(scope), "POST", "/api/v1/assistant/tool-call", InferenceAllowed)).Status.Should().Be(403);
        (await Run(Scoped(scope), "GET", "/api/v1/assistant/tool-catalog", InferenceAllowed)).Status.Should().Be(403);
        (await Run(Scoped(scope), "GET", CalibrationPath, EngineAllowed)).Status.Should().Be(403);
    }

    [Fact]
    public async Task Engine_allows_calibration_only_for_admin_scope()
    {
        (await Run(Scoped("assistant.tools"), "GET", CalibrationPath, EngineAllowed)).Status.Should().Be(403);
        (await Run(Scoped("assistant.admin"), "GET", CalibrationPath, EngineAllowed)).NextCalled.Should().BeTrue();
        (await Run(Scoped("assistant.admin"), "GET", "/api/v1/workflow-engine/admin/anything", EngineAllowed)).Status.Should().Be(403);
        (await Run(Scoped("assistant.admin"), "POST", CalibrationPath, EngineAllowed)).Status.Should().Be(403);
    }

    [Fact]
    public async Task Unscoped_and_unauthenticated_pass_through()
    {
        (await Run(Principal(("org", "o")), "PUT", "/api/v1/projects/p1/workflows/w1", InferenceAllowed)).NextCalled.Should().BeTrue();
        (await Run(null, "GET", "/api/v1/billing/summary", EngineAllowed)).NextCalled.Should().BeTrue();
    }

    private static TokenValidationParameters Parameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
        ValidateLifetime = true
    };

    private static async Task<ClaimsPrincipal> Validate(string token)
    {
        TokenValidationResult result = await new JsonWebTokenHandler { MapInboundClaims = true }.ValidateTokenAsync(token, Parameters());
        result.IsValid.Should().BeTrue();
        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

    private static string Mint(string? scope)
    {
        Dictionary<string, object> claims = new() { ["sub"] = Guid.NewGuid().ToString() };
        if (scope is not null) claims["scope"] = scope;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256)
        });
    }

    [Fact]
    public async Task Real_jwt_validation_keeps_the_scope_claim_and_the_guard_refuses_it()
    {
        ClaimsPrincipal scoped = await Validate(Mint("assistant.tools"));
        McpScopeGuard.IsScoped(scoped).Should().BeTrue();
        (await Run(scoped, "GET", "/api/v1/billing/summary", InferenceAllowed)).Status.Should().Be(403);
        (await Run(scoped, "GET", "/api/v1/assistant/tool-catalog", InferenceAllowed)).NextCalled.Should().BeTrue();

        ClaimsPrincipal session = await Validate(Mint(null));
        (await Run(session, "GET", "/api/v1/billing/summary", InferenceAllowed)).NextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task On_behalf_of_token_passes_everywhere()
    {
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
            ["Jwt:SigningKey"] = Key
        }).Build();
        string token = new OnBehalfOfTokenIssuer(cfg).Issue(Guid.NewGuid(), "a@b.c", Guid.NewGuid(), OrganizationRole.Owner, Guid.NewGuid());
        ClaimsPrincipal user = await Validate(token);
        McpScopeGuard.IsScoped(user).Should().BeFalse();
        (await Run(user, "PUT", "/api/v1/projects/p1/workflows/w1", InferenceAllowed)).NextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Runs_watch_token_reaches_only_tool_call()
    {
        (await Run(Scoped("runs.watch"), "POST", "/api/v1/assistant/tool-call", InferenceAllowed)).NextCalled.Should().BeTrue();
        (await Run(Scoped("runs.watch"), "GET", "/api/v1/assistant/tool-catalog", InferenceAllowed)).Status.Should().Be(403);
        (await Run(Scoped("runs.watch"), "GET", CalibrationPath, EngineAllowed)).Status.Should().Be(403);
        (await Run(Scoped("runs.watch"), "POST", CalibrationPath, EngineAllowed)).Status.Should().Be(403);
        foreach ((string method, string path) in new[]
                 {
                     ("GET", "/api/v1/projects"), ("POST", "/api/v1/assistant/chat"), ("GET", "/api/v1/billing/summary"),
                     ("PUT", "/api/v1/projects/p1/workflows/w1"), ("POST", "/api/v1/assistant/tool-call/extra"),
                     ("GET", "/api/v1/assistant/tool-call"), ("GET", "/api/v1/executions/e1")
                 })
        {
            var r = await Run(Scoped("runs.watch"), method, path, InferenceAllowed);
            r.Status.Should().Be(403, path);
            r.NextCalled.Should().BeFalse(path);
        }
    }

    [Fact]
    public void Engine_allowlist_has_no_runs_watch_entry()
    {
        EngineRoutes.Allowed.SelectMany(r => r.AnyOfScopes).Should().NotContain("runs.watch");
    }

    [Fact]
    public void Allowlist_matches_AssistantToolBridgeController_routes()
    {
        Type t = typeof(AssistantToolBridgeController);
        string prefix = "/" + t.GetCustomAttribute<RouteAttribute>()!.Template;
        string Template<T>(string method) where T : HttpMethodAttribute =>
            t.GetMethods().Select(m => m.GetCustomAttribute<T>()).First(a => a is not null && a.Template is not null && a.HttpMethods.Contains(method))!.Template!;
        InferenceRoutes.ToolCatalog.Should().Be(prefix + "/" + Template<HttpGetAttribute>("GET"));
        InferenceRoutes.ToolCall.Should().Be(prefix + "/" + Template<HttpPostAttribute>("POST"));
        InferenceRoutes.RunsWatchScope.Should().Be("runs.watch");
        InferenceRoutes.Allowed.Should().HaveCount(4);
        InferenceRoutes.Allowed.Should().ContainSingle(r => r.Method == "POST" && r.Path == InferenceRoutes.ToolCall && r.AnyOfScopes.SequenceEqual(new[] { "runs.watch" }));
    }

    [Fact]
    public void Engine_allowlist_matches_DecisionCalibrationController_and_client_path()
    {
        Type t = typeof(ReelBolt.WorkflowEngine.Controllers.DecisionCalibrationController);
        string route = "/" + t.GetCustomAttribute<RouteAttribute>()!.Template;
        t.GetMethods().Any(m => m.GetCustomAttribute<HttpGetAttribute>() is { Template: null }).Should().BeTrue();
        EngineRoutes.DecisionCalibration.Should().Be(route);
        EngineRoutes.Allowed.Should().ContainSingle().Which.Should().BeEquivalentTo(new McpAllowedRoute("GET", route, "assistant.admin"));
        ReelBolt.Inference.Api.Services.Assistant.DecisionCalibrationClient.CalibrationPath.Should().Be(route);
    }

    [Theory]
    [InlineData("src/ReelBolt.Inference.Api/Program.cs", "UseMcpScopeGuard(McpScopeRoutes.Allowed)")]
    [InlineData("src/ReelBolt.WorkflowEngine/Program.cs", "UseMcpScopeGuard(McpScopeRoutes.Allowed)")]
    public void Guard_is_wired_between_authentication_and_authorization(string relative, string guardCall)
    {
        // Source-order test: no Mvc.Testing/TestHost reference exists and both hosts need DB/Redis to start.
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ReelBolt.sln"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the .sln must be above the test assembly");
        string src = File.ReadAllText(Path.Combine(dir!, relative.Replace('/', Path.DirectorySeparatorChar)));
        int authn = src.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);
        int guard = src.IndexOf("app." + guardCall, StringComparison.Ordinal);
        int authz = src.IndexOf("app.UseAuthorization()", StringComparison.Ordinal);
        authn.Should().BeGreaterThanOrEqualTo(0);
        guard.Should().BeGreaterThan(authn, "the guard must run after UseAuthentication");
        authz.Should().BeGreaterThan(guard, "the guard must run before UseAuthorization");
    }
}
