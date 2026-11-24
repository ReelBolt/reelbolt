using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>
/// Q-M4 through the real pipeline order (authentication, then routing, then the real
/// <see cref="McpScopeGuardMiddleware"/> and the real <see cref="ApiKeyScopeRefusalAuditor"/>): a key lacking the
/// endpoint's scope gets 403 and exactly one audit entry carrying the route TEMPLATE.
/// </summary>
public sealed class ScopeRefusalPipelineTests
{
    private sealed class Capture : IApiKeyAuditWriter
    {
        public List<ApiKeyAuditEntry> Entries { get; } = [];
        public void TryWrite(ApiKeyAuditEntry entry) => Entries.Add(entry);
    }

    private const string Template = "api/public/v1/projects/{projectId:guid}/workflows/{workflowId:guid}/runs";

    [Fact]
    public async Task A_key_without_the_endpoint_scope_gets_403_and_one_audit_entry_with_the_route_template()
    {
        Capture audit = new();
        ServiceCollection services = new();
        services.AddSingleton<IScopeRefusalAuditor>(new ApiKeyScopeRefusalAuditor(audit));
        using ServiceProvider sp = services.BuildServiceProvider();
        Guid keyId = Guid.NewGuid();
        bool reached = false;

        IApplicationBuilder app = new ApplicationBuilder(sp);
        // authentication
        app.Use(async (ctx, next) =>
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "u"), new Claim(AuthClaims.ApiKeyId, keyId.ToString()), new Claim(AuthClaims.Scope, ApiScopes.RunsRead)], "ApiKey"));
            await next();
        });
        // routing: selects the public endpoint and its metadata, as UseRouting does
        app.Use(async (ctx, next) =>
        {
            ctx.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(Template), 0,
                new EndpointMetadataCollection(new PublicApiAudienceAttribute(), new RequireScopeAttribute(ApiScopes.RunsExecute)), "runs.start"));
            await next();
        });
        app.UseMcpScopeGuard();
        app.Run(_ => { reached = true; return Task.CompletedTask; });
        RequestDelegate pipeline = app.Build();

        string secretPath = $"/api/public/v1/projects/{Guid.NewGuid()}/workflows/{Guid.NewGuid()}/runs";
        DefaultHttpContext http = new() { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = secretPath;
        http.Response.Body = new MemoryStream();

        await pipeline(http);

        http.Response.StatusCode.Should().Be(403);
        reached.Should().BeFalse();
        audit.Entries.Should().ContainSingle();
        audit.Entries[0].ApiKeyId.Should().Be(keyId);
        audit.Entries[0].StatusCode.Should().Be(403);
        audit.Entries[0].Route.Should().Be(Template).And.NotContain(secretPath);
    }
}
