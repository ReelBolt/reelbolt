using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.ApiKeys.ApiKeyTestSupport;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>public-api T-023: S-M3c, S-M5, S-L4 and the scope-refusal audit.</summary>
public class ReviewFixRevokeTests
{
    private sealed class Capture : IApiKeyAuditWriter
    {
        public List<ApiKeyAuditEntry> Entries { get; } = [];
        public void TryWrite(ApiKeyAuditEntry entry) => Entries.Add(entry);
    }

    private sealed class OkStore : IApiKeyRateCounterStore
    {
        public Task<(int Total, int Writes)> IncrementAsync(Guid apiKeyId, DateTime windowStart, bool isWrite, CancellationToken ct) =>
            Task.FromResult((1, 0));

        public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class HotStore : IApiKeyRateCounterStore
    {
        public Task<(int Total, int Writes)> IncrementAsync(Guid apiKeyId, DateTime windowStart, bool isWrite, CancellationToken ct) =>
            Task.FromResult((100_000, 100_000));

        public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct) => Task.FromResult(0);
    }

    private static IOptionsMonitor<PublicApiOptions> Opts()
    {
        Mock<IOptionsMonitor<PublicApiOptions>> m = new();
        m.SetupGet(o => o.CurrentValue).Returns(new PublicApiOptions { Enabled = true });
        return m.Object;
    }

    [Theory]
    [InlineData(false, "POST", "api/public/v1/projects/{projectId:guid}/workflows/{workflowId:guid}/runs", false, 402)]
    [InlineData(false, "POST", "api/public/v1/uploads", false, 402)]
    [InlineData(false, "POST", "api/public/v1/webhooks", false, 402)]
    [InlineData(false, "DELETE", "api/public/v1/webhooks/{id:guid}", false, 402)]
    [InlineData(false, "GET", "api/public/v1/apps", true, 200)]
    [InlineData(false, "POST", "api/public/v1/projects/{projectId:guid}/workflows/{workflowId:guid}/estimate", true, 200)]
    [InlineData(true, "POST", "api/public/v1/uploads", true, 200)]
    public async Task Guard_keeps_a_trial_key_read_only_per_request(bool apiAccess, string method, string template, bool reachesAction, int expected)
    {
        Guid org = Guid.NewGuid();
        Mock<IOrganizationEntitlements> ent = new();
        ent.Setup(e => e.GetAsync(org, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { ApiAccess = apiAccess, Tier = "trial" }));
        PublicApiGuardFilter f = new(Opts(), new FakeKeyContext(), new ApiKeyRateLimiter(new OkStore(), Opts(), NullLogger<ApiKeyRateLimiter>.Instance),
            new Capture(), null, ent.Object);

        DefaultHttpContext http = new();
        http.Request.Method = method;
        http.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, EndpointMetadataCollection.Empty, "t"));
        http.Response.Body = new MemoryStream();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString()), new Claim(AuthClaims.Scope, "runs.read"), new Claim("org", org.ToString())], "ApiKey"));
        ResourceExecutingContext ctx = new(new ActionContext(http, new RouteData(),
            new ActionDescriptor { EndpointMetadata = [new AnyApiKeyScopeAttribute()] }), [], []);
        bool reached = false;
        await f.OnResourceExecutionAsync(ctx, () =>
        {
            reached = true;
            return Task.FromResult(new ResourceExecutedContext(ctx, []));
        });

        reached.Should().Be(reachesAction);
        if (!reachesAction)
        {
            http.Response.StatusCode.Should().Be(expected);
            http.Response.Body.Position = 0;
            JsonNode body = JsonNode.Parse(new StreamReader(http.Response.Body).ReadToEnd())!;
            body["error"]!["code"]!.GetValue<string>().Should().Be(PlanGate.ApiPlanReadOnlyCode);
        }
    }

    [Fact]
    public async Task A_refused_trial_write_is_still_rate_limited_first()
    {
        Guid org = Guid.NewGuid();
        Mock<IOrganizationEntitlements> ent = new();
        ent.Setup(e => e.GetAsync(org, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { ApiAccess = false, Tier = "trial" }));
        PublicApiGuardFilter f = new(Opts(), new FakeKeyContext(), new ApiKeyRateLimiter(new HotStore(), Opts(), NullLogger<ApiKeyRateLimiter>.Instance),
            new Capture(), null, ent.Object);
        DefaultHttpContext http = new();
        http.Request.Method = "POST";
        http.Response.Body = new MemoryStream();
        http.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("api/public/v1/uploads"), 0, EndpointMetadataCollection.Empty, "t"));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString()), new Claim(AuthClaims.Scope, "runs.read"), new Claim("org", org.ToString())], "ApiKey"));
        ResourceExecutingContext ctx = new(new ActionContext(http, new RouteData(),
            new ActionDescriptor { EndpointMetadata = [new AnyApiKeyScopeAttribute()] }), [], []);

        await f.OnResourceExecutionAsync(ctx, () => throw new InvalidOperationException("must not reach the action"));

        http.Response.StatusCode.Should().Be(429, "the limiter counts and throttles a refused trial write like any call");
    }

    [Fact]
    public async Task Scope_refusal_of_a_key_is_audited_with_the_route_template_or_unmatched_never_the_path()
    {
        Capture audit = new();
        ServiceCollection services = new();
        services.AddSingleton<IScopeRefusalAuditor>(new ApiKeyScopeRefusalAuditor(audit));
        using ServiceProvider sp = services.BuildServiceProvider();
        Guid keyId = Guid.NewGuid();

        DefaultHttpContext ctx = new() { RequestServices = sp };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "u"), new Claim(AuthClaims.ApiKeyId, keyId.ToString()), new Claim(AuthClaims.Scope, "runs.read")], "ApiKey"));
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/api/v1/projects/secret-project-id/anything";
        ctx.Response.Body = new MemoryStream();
        McpScopeGuardMiddleware mw = new(_ => Task.CompletedTask, []);
        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(403);
        audit.Entries.Should().ContainSingle();
        audit.Entries[0].ApiKeyId.Should().Be(keyId);
        audit.Entries[0].StatusCode.Should().Be(403);
        audit.Entries[0].Route.Should().Be("(unmatched)").And.NotContain("secret");

        // A non-key principal (an MCP token) is not audited here.
        DefaultHttpContext mcp = new() { RequestServices = sp };
        mcp.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u"), new Claim(AuthClaims.Scope, "assistant.tools")], "t"));
        mcp.Request.Method = "POST";
        mcp.Request.Path = "/api/v1/billing/summary";
        mcp.Response.Body = new MemoryStream();
        await mw.InvokeAsync(mcp);
        audit.Entries.Should().ContainSingle();
    }

    private static async Task<(MembershipValidator V, InferenceApiDbContext Db, Guid User, Guid Org, ApiKey Key, Project A, Project B)> SeedKey(
        Action<ApiKey>? tweak = null)
    {
        InferenceApiDbContext db = NewDb();
        var (user, org) = await SeedCreatorAsync(db);
        Project a = new() { Id = Guid.NewGuid(), Name = "A", OrganizationId = org.Id, OwnerId = user.Id };
        Project b = new() { Id = Guid.NewGuid(), Name = "B", OrganizationId = org.Id, OwnerId = user.Id };
        db.Projects.AddRange(a, b);
        ApiKey key = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            ServiceUserId = user.Id,
            CreatedByUserId = user.Id,
            Name = "k",
            KeyId = Guid.NewGuid().ToString("N")[..12],
            SecretHash = "h",
            LastFour = "abcd",
            Scopes = ["runs.read"],
            ProjectIds = [a.Id]
        };
        tweak?.Invoke(key);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();
        return (new MembershipValidator(new MemoryCache(new MemoryCacheOptions()), NullLogger<MembershipValidator>.Instance), db, user.Id, org.Id, key, a, b);
    }

    private static ClaimsPrincipal Obo(Guid user, Guid org, Guid? keyId)
    {
        ClaimsIdentity id = new("test");
        id.AddClaim(new Claim("sub", user.ToString()));
        id.AddClaim(new Claim("org", org.ToString()));
        if (keyId is { } k) id.AddClaim(new Claim(AuthClaims.ApiKeyId, k.ToString()));
        return new ClaimsPrincipal(id);
    }

    [Fact]
    public async Task OnBehalfOf_active_key_fills_the_context_and_engine_callbacks_get_404_on_other_projects()
    {
        var (v, db, user, org, key, a, b) = await SeedKey();
        DefaultHttpContext http = new();
        (await v.AuthorizeOnBehalfOfKeyAsync(http, Obo(user, org, key.Id), user, org, db, default)).Should().BeNull();

        HttpApiKeyContext keyCtx = new(new HttpContextAccessor { HttpContext = http });
        keyCtx.KeyId.Should().Be(key.Id);
        FakeUser current = new(org, OrganizationRole.Member, user);
        ProjectAccessService access = new(db, current, keyCtx);
        (await access.GetAsync(a.Id, ProjectPermission.View, default)).Outcome.Should().Be(ProjectAccessOutcome.Found);
        (await access.GetAsync(b.Id, ProjectPermission.View, default)).Outcome.Should().Be(ProjectAccessOutcome.NotFound);
    }

    [Fact]
    public async Task OnBehalfOf_refuses_a_missing_revoked_or_expired_key()
    {
        var (v, db, user, org, key, _, _) = await SeedKey();
        DefaultHttpContext http = new();
        (await v.AuthorizeOnBehalfOfKeyAsync(http, Obo(user, org, null), user, org, db, default)).Should().NotBeNull();
        (await v.AuthorizeOnBehalfOfKeyAsync(http, Obo(user, org, Guid.NewGuid()), user, org, db, default)).Should().NotBeNull();

        key.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        (await v.AuthorizeOnBehalfOfKeyAsync(http, Obo(user, org, key.Id), user, org, db, default)).Should().NotBeNull();

        key.ExpiresAt = null;
        key.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        (await v.AuthorizeOnBehalfOfKeyAsync(http, Obo(user, org, key.Id), user, org, db, default)).Should().NotBeNull();
        http.Items.Should().BeEmpty();
    }
}
