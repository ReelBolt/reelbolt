using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;
using Xunit;
using InferenceRoutes = ReelBolt.Inference.Api.McpScopeRoutes;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

public class PublicApiScopeEnforcementTests
{
    private static ClaimsPrincipal Key(params string[] scopes) => new(new ClaimsIdentity(
        [new Claim("sub", "u"), new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString()), new Claim(AuthClaims.Scope, string.Join(' ', scopes))], "ApiKey"));

    private static ClaimsPrincipal McpToken(params string[] scopes) => new(new ClaimsIdentity(
        [new Claim("sub", "u"), new Claim(AuthClaims.Scope, string.Join(' ', scopes))], "Bearer"));

    private static object[] Meta(params object[] items) => items;

    [Fact]
    public void Evaluate_any_key_scope_allows_a_key_with_no_scopes()
    {
        PublicApiScopeCheck.Evaluate(Key(), Meta(new AnyApiKeyScopeAttribute())).Verdict.Should().Be(PublicApiScopeVerdict.Allowed);
    }

    [Fact]
    public void Evaluate_require_scope_allows_with_one_held_and_refuses_without()
    {
        object[] meta = Meta(new RequireScopeAttribute("runs.read", "runs.execute"));
        PublicApiScopeCheck.Evaluate(Key("runs.execute"), meta).Verdict.Should().Be(PublicApiScopeVerdict.Allowed);
        PublicApiScopeResult r = PublicApiScopeCheck.Evaluate(Key("files.read"), meta);
        r.Verdict.Should().Be(PublicApiScopeVerdict.Insufficient);
        r.RequiredScopes.Should().BeEquivalentTo("runs.read", "runs.execute");
    }

    [Fact]
    public void Evaluate_undeclared_endpoint_is_refused_even_for_a_key_with_every_scope()
    {
        PublicApiScopeCheck.Evaluate(Key(ApiScopes.All.ToArray()), Meta()).Verdict.Should().Be(PublicApiScopeVerdict.Undeclared);
        PublicApiScopeCheck.Evaluate(Key(), null).Verdict.Should().Be(PublicApiScopeVerdict.Undeclared);
    }

    private static async Task<(int Status, bool Next, string Body)> RunMiddleware(ClaimsPrincipal user, params object[] metadata)
    {
        bool next = false;
        McpScopeGuardMiddleware mw = new(_ => { next = true; return Task.CompletedTask; }, InferenceRoutes.Allowed);
        DefaultHttpContext ctx = new() { User = user };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/public/v1/x";
        ctx.Response.Body = new MemoryStream();
        ctx.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection([new PublicApiAudienceAttribute(), .. metadata]), "t"));
        await mw.InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, next, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Middleware_key_without_the_scope_gets_403_insufficient_scope()
    {
        (int status, bool next, string body) = await RunMiddleware(Key("files.read"), new RequireScopeAttribute("runs.read"));
        status.Should().Be(403);
        next.Should().BeFalse();
        JsonNode err = JsonNode.Parse(body)!["error"]!;
        err["code"]!.GetValue<string>().Should().Be("insufficient_scope");
        err["requiredScopes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("runs.read");
    }

    [Fact]
    public async Task Middleware_key_with_the_scope_passes()
    {
        (_, bool next, _) = await RunMiddleware(Key("runs.read"), new RequireScopeAttribute("runs.read"));
        next.Should().BeTrue();
    }

    [Fact]
    public async Task Middleware_undeclared_public_endpoint_is_403_scope_not_declared()
    {
        (int status, bool next, string body) = await RunMiddleware(Key(ApiScopes.All.ToArray()));
        status.Should().Be(403);
        next.Should().BeFalse();
        JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be("scope_not_declared");
    }

    [Fact]
    public async Task Middleware_mcp_token_still_reaches_the_filter_for_its_401()
    {
        (_, bool next, _) = await RunMiddleware(McpToken("runs.read"));
        next.Should().BeTrue();
    }

    private sealed class Monitor(PublicApiOptions v) : IOptionsMonitor<PublicApiOptions>
    {
        public PublicApiOptions CurrentValue => v;
        public PublicApiOptions Get(string? name) => v;
        public IDisposable? OnChange(Action<PublicApiOptions, string?> listener) => null;
    }

    private sealed class NullStore : IApiKeyRateCounterStore
    {
        public Task<(int Total, int Writes)> IncrementAsync(Guid k, DateTime w, bool isWrite, System.Threading.CancellationToken ct) => Task.FromResult((1, 0));
        public Task<int> DeleteOlderThanAsync(DateTime w, System.Threading.CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class Audit : IApiKeyAuditWriter
    {
        public List<ApiKeyAuditEntry> Entries { get; } = [];
        public void TryWrite(ApiKeyAuditEntry entry) => Entries.Add(entry);
    }

    private sealed class KeyCtx : IApiKeyContext
    {
        public bool IsApiKey => true;
        public Guid? KeyId => null;
        public Guid? OrganizationId => null;
        public IReadOnlyList<Guid>? AllowedProjectIds => null;
        public IReadOnlyCollection<string> Scopes => [];
    }

    private static async Task<(bool Reached, int Status, string Body, Audit Audit)> RunFilter(ClaimsPrincipal user, params object[] metadata)
    {
        PublicApiOptions o = new() { Enabled = true };
        Audit audit = new();
        PublicApiGuardFilter f = new(new Monitor(o), new KeyCtx(), new ApiKeyRateLimiter(new NullStore(), new Monitor(o), NullLogger<ApiKeyRateLimiter>.Instance), audit);
        DefaultHttpContext http = new() { User = user };
        http.Response.Body = new MemoryStream();
        ResourceExecutingContext ctx = new(new ActionContext(http, new RouteData(), new ActionDescriptor { EndpointMetadata = metadata }), [], []);
        bool reached = false;
        await f.OnResourceExecutionAsync(ctx, () =>
        {
            reached = true;
            return Task.FromResult(new ResourceExecutedContext(ctx, []));
        });
        http.Response.Body.Position = 0;
        return (reached, http.Response.StatusCode, new StreamReader(http.Response.Body).ReadToEnd(), audit);
    }

    [Fact]
    public async Task Filter_key_without_the_scope_is_403_and_audited()
    {
        (bool reached, int status, string body, Audit audit) = await RunFilter(Key("files.read"), new RequireScopeAttribute("runs.read"));
        reached.Should().BeFalse();
        status.Should().Be(403);
        JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be("insufficient_scope");
        audit.Entries.Should().ContainSingle().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Filter_key_with_the_scope_passes()
    {
        (bool reached, _, _, _) = await RunFilter(Key("runs.read"), new RequireScopeAttribute("runs.read"));
        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Filter_undeclared_endpoint_is_403_scope_not_declared()
    {
        (bool reached, int status, string body, _) = await RunFilter(Key(ApiScopes.All.ToArray()));
        reached.Should().BeFalse();
        status.Should().Be(403);
        JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be("scope_not_declared");
    }

    /// <summary>Every action of every public controller (current and future), as metadata like the endpoint carries.</summary>
    public static IEnumerable<object[]> PublicActions()
    {
        Assembly asm = typeof(MeController).Assembly;
        foreach (Type type in asm.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t)))
        {
            bool publicController = typeof(PublicApiControllerBase).IsAssignableFrom(type)
                || type.GetCustomAttribute<PublicApiAudienceAttribute>(true) is not null;
            // L17: every public action, inherited ones included, with no HttpMethodAttribute / DeclaredOnly filter.
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object) && m.DeclaringType != typeof(ControllerBase)
                                     && m.GetCustomAttribute<NonActionAttribute>() is null))
            {
                if (publicController || m.GetCustomAttribute<PublicApiAudienceAttribute>(true) is not null)
                    yield return [type.Name + "." + m.Name];
            }
        }
    }

    private static (object[] Metadata, bool Any, string[] Required) MetaOf(string id)
    {
        string[] parts = id.Split('.');
        Type type = typeof(MeController).Assembly.GetTypes().Single(t => t.Name == parts[0]);
        MethodInfo method = type.GetMethod(parts[1], BindingFlags.Public | BindingFlags.Instance)!;
        object[] meta = [.. method.GetCustomAttributes(true)];
        return (meta, meta.OfType<AnyApiKeyScopeAttribute>().Any(),
            meta.OfType<RequireScopeAttribute>().SelectMany(a => a.AnyOf).Distinct().ToArray());
    }

    [Fact]
    public void Route_table_has_public_actions()
    {
        PublicActions().Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(PublicActions))]
    public void Every_public_action_declares_a_known_scope_or_any_key_and_enforces_it(string id)
    {
        (object[] meta, bool any, string[] required) = MetaOf(id);
        if (any)
        {
            id.Should().Be("MeController.Get", "MeController.Get is the only action allowed to use AnyApiKeyScope");
            PublicApiScopeCheck.Evaluate(Key(), meta).Verdict.Should().Be(PublicApiScopeVerdict.Allowed);
            return;
        }

        required.Should().NotBeEmpty($"{id} must declare RequireScope or AnyApiKeyScope");
        required.Should().OnlyContain(s => ApiScopes.IsKnown(s));
        string[] others = ApiScopes.All.Except(required).ToArray();
        PublicApiScopeCheck.Evaluate(Key(others), meta).Verdict.Should().Be(PublicApiScopeVerdict.Insufficient);
        PublicApiScopeCheck.Evaluate(Key(required[0]), meta).Verdict.Should().Be(PublicApiScopeVerdict.Allowed);
    }
}
