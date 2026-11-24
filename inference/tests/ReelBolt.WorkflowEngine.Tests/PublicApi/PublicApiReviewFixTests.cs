using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Inference;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.ApiKeys.ApiKeyTestSupport;
using EngineRoutes = ReelBolt.WorkflowEngine.McpScopeRoutes;
using InferenceRoutes = ReelBolt.Inference.Api.McpScopeRoutes;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>public-api T-028: scope sweep over the real endpoint table, scopes in the service, M9 and the engine key check.</summary>
public class PublicApiReviewFixTests
{
    private const string PublicPrefix = "api/public/";

    private static List<RouteEndpoint> RealPublicEndpoints()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Services.AddControllers().AddApplicationPart(typeof(MeController).Assembly);
        WebApplication app = builder.Build();
        app.MapControllers();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimStart('/').StartsWith(PublicPrefix, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
    }

    [Fact]
    public void Every_api_public_endpoint_declares_a_scope_on_its_method_and_runs_the_guard()
    {
        List<RouteEndpoint> endpoints = RealPublicEndpoints();
        endpoints.Should().NotBeEmpty();
        List<string> problems = [];
        foreach (RouteEndpoint e in endpoints)
        {
            string name = $"{e.RoutePattern.RawText} ({e.DisplayName})";
            ControllerActionDescriptor? action = e.Metadata.GetMetadata<ControllerActionDescriptor>();
            if (action is null) { problems.Add($"{name}: not a controller action, so it has no declared scope"); continue; }

            if (e.Metadata.GetMetadata<PublicApiAudienceAttribute>() is null) problems.Add($"{name}: no PublicApiAudience");
            if (!e.Metadata.OfType<ServiceFilterAttribute>().Any(f => f.ServiceType == typeof(PublicApiGuardFilter)))
                problems.Add($"{name}: no PublicApiGuardFilter");

            object[] own = action.MethodInfo.GetCustomAttributes(inherit: true);
            bool any = own.OfType<AnyApiKeyScopeAttribute>().Any();
            string[] required = own.OfType<RequireScopeAttribute>().SelectMany(a => a.AnyOf).ToArray();
            if (!any && required.Length == 0) problems.Add($"{name}: no RequireScope or AnyApiKeyScope on the action");
            foreach (string scope in required.Where(s => !ApiScopes.IsKnown(s))) problems.Add($"{name}: unknown scope {scope}");

            // Q-L4: AnyApiKeyScope is for MeController only.
            if (any && action.ControllerTypeInfo.AsType() != typeof(MeController))
                problems.Add($"{name}: AnyApiKeyScope outside MeController");

            // L16: class-level declarations are forbidden (the method alone decides).
            if (action.ControllerTypeInfo.GetCustomAttributes(inherit: true)
                .Any(a => a is RequireScopeAttribute or AnyApiKeyScopeAttribute))
                problems.Add($"{name}: class-level RequireScope/AnyApiKeyScope");
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void The_only_api_public_minimal_endpoint_is_the_anonymous_openapi_document()
    {
        string program = System.IO.File.ReadAllText(FindFile("inference/src/ReelBolt.Inference.Api/Program.cs"));
        System.Text.RegularExpressions.Regex.Matches(program, "\"/api/public[^\"]*\"").Select(m => m.Value)
            .Should().OnlyContain(v => v == "\"/api/public/v1/openapi.json\"");
    }

    private static string FindFile(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, relative))) dir = System.IO.Path.GetDirectoryName(dir);
        return dir is null ? throw new InvalidOperationException(relative) : System.IO.Path.Combine(dir, relative);
    }

    // ------------------------------------------------------------------ L16

    private sealed class Klass
    {
        [RequireScope("files.read")]
        public void Act() { }
    }

    [RequireScope("files.read")]
    private sealed class WithClassScope
    {
        [RequireScope("runs.read")]
        public void Act() { }
    }

    private static ClaimsPrincipal Key(params string[] scopes) => new(new ClaimsIdentity(
        [new Claim("sub", "u"), new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString()), new Claim(AuthClaims.Scope, string.Join(' ', scopes))], "ApiKey"));

    [Fact]
    public void Evaluate_with_the_action_ignores_class_level_scope_metadata()
    {
        MethodInfo method = typeof(WithClassScope).GetMethod(nameof(WithClassScope.Act))!;
        object[] endpointMetadata = [.. typeof(WithClassScope).GetCustomAttributes(), .. method.GetCustomAttributes()];

        PublicApiScopeCheck.Evaluate(Key("files.read"), endpointMetadata, method).Verdict.Should().Be(PublicApiScopeVerdict.Insufficient);
        PublicApiScopeCheck.Evaluate(Key("runs.read"), endpointMetadata, method).Verdict.Should().Be(PublicApiScopeVerdict.Allowed);
    }

    // ------------------------------------------------------------------ L18

    [Fact]
    public async Task Creating_a_key_with_an_unknown_scope_throws_in_the_service()
    {
        InferenceApiDbContext db = NewDb();
        var (creator, org) = await SeedCreatorAsync(db);

        Func<Task> act = () => Service(db).CreateAsync(Command(org.Id, creator.Id, scopes: ["runs.read", "assistant.tools"]), default);

        await act.Should().ThrowAsync<ArgumentException>();
        db.ApiKeys.Should().BeEmpty();
    }

    [Fact]
    public void Public_key_scopes_and_the_mcp_allowlist_scopes_never_overlap_in_either_service()
    {
        string[] mcp = [.. InferenceRoutes.Allowed.SelectMany(r => r.AnyOfScopes), .. EngineRoutes.Allowed.SelectMany(r => r.AnyOfScopes)];
        mcp.Should().NotBeEmpty().And.Contain("runs.watch").And.Contain("assistant.admin");
        ApiScopes.All.Intersect(mcp).Should().BeEmpty();
        InferenceRoutes.Allowed.SelectMany(r => r.AnyOfScopes).Intersect(ApiScopes.All).Should().BeEmpty();
        EngineRoutes.Allowed.SelectMany(r => r.AnyOfScopes).Intersect(ApiScopes.All).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ M9

    [Fact]
    public async Task M9_the_on_behalf_of_key_is_found_with_an_http_backed_tenant_and_no_principal_yet()
    {
        string store = Guid.NewGuid().ToString();
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(store).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        Guid user, org, keyId;
        using (InferenceApiDbContext seed = new(options))
        {
            var (creator, o) = await SeedCreatorAsync(seed);
            user = creator.Id; org = o.Id; keyId = Guid.NewGuid();
            seed.ApiKeys.Add(new ApiKey
            {
                Id = keyId,
                OrganizationId = org,
                ServiceUserId = user,
                CreatedByUserId = user,
                Name = "k",
                KeyId = Guid.NewGuid().ToString("N")[..12],
                SecretHash = "h",
                LastFour = "abcd",
                Scopes = ["runs.read"]
            });
            await seed.SaveChangesAsync();
        }

        // OnTokenValidated: HttpContext.User is not set yet, so the Tenant filter has no organization.
        DefaultHttpContext http = new();
        TenantContext tenant = new(new HttpContextAccessor { HttpContext = http });
        tenant.OrganizationId.Should().BeNull();
        using InferenceApiDbContext db = new(options, tenant);
        db.ApiKeys.Count().Should().Be(0, "the filter alone would hide the key, which is the bug");

        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim("sub", user.ToString()), new Claim("org", org.ToString()), new Claim(AuthClaims.ApiKeyId, keyId.ToString())], "t"));
        MembershipValidator v = new(new MemoryCache(new MemoryCacheOptions()), NullLogger<MembershipValidator>.Instance);

        (await v.AuthorizeOnBehalfOfKeyAsync(http, principal, user, org, db, default)).Should().BeNull();
        http.Items.Should().ContainKey(HttpApiKeyContext.ItemKey);
    }

    // ------------------------------------------------------------------ M7/M8 engine

    [Fact]
    public async Task The_engine_mints_no_identity_for_a_key_run_of_a_soft_deleted_organization()
    {
        string name = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        ServiceProvider sp = services.BuildServiceProvider();
        Guid org = Guid.NewGuid(), svc = Guid.NewGuid(), key = Guid.NewGuid(), run = Guid.NewGuid();
        using (IServiceScope scope = sp.CreateScope())
        {
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.Organizations.Add(new Organization { Id = org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
            db.ApplicationUsers.Add(new ApplicationUser { Id = svc, Email = "apikey+k@service.invalid", IsServiceAccount = true });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org, UserId = svc, Role = OrganizationRole.Member });
            db.ApiKeys.Add(new ApiKey
            {
                Id = key,
                OrganizationId = org,
                ServiceUserId = svc,
                CreatedByUserId = svc,
                Name = "k",
                KeyId = "abcdefghijkl",
                SecretHash = "h",
                LastFour = "abcd",
                Scopes = ["runs.read"]
            });
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = run, OrganizationId = org, InitiatedByUserId = svc, ApiKeyId = key });
            await db.SaveChangesAsync();
        }

        ExecutionUserResolver resolver = new(sp.GetRequiredService<IServiceScopeFactory>());
        (await resolver.ResolveAsync(run, default)).Should().NotBeNull("the org is live");

        using (IServiceScope scope = sp.CreateScope())
        {
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.Organizations.Single(o => o.Id == org).DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        (await resolver.ResolveAsync(run, default)).Should().BeNull();
    }
}
