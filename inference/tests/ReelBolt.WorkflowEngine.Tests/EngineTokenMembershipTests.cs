#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine;
using ReelBolt.WorkflowEngine.Controllers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Decision;
using ReelBolt.WorkflowEngine.Services.Skills;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Incident H3: the engine's JwtBearer must re-check the database like the Inference API does. Drives the REAL
/// <c>OnTokenValidated</c> event from <c>AddWorkflowEngineServices</c> against an in-memory engine context, then
/// feeds the resulting principal to the admin-gated controllers.
/// </summary>
public sealed class EngineTokenMembershipTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static JwtBearerEvents RealEvents()
    {
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine", (services, configuration) => services.AddWorkflowEngineServices(configuration));
        return root.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).Events;
    }

    private sealed class Env : IDisposable
    {
        public ServiceProvider Services { get; }
        public Env()
        {
            string name = Guid.NewGuid().ToString();
            ServiceCollection sc = new();
            sc.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(name));
            sc.AddMemoryCache();
            sc.AddSingleton<TokenMembershipValidator>();
            Services = sc.BuildServiceProvider();
        }

        public async Task<Guid> SeedUser(bool isAdmin, DateTime? invalidatedAt = null, bool serviceAccount = false)
        {
            using IServiceScope scope = Services.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            Guid id = Guid.NewGuid();
            db.ApplicationUsers.Add(new ApplicationUser
            {
                Id = id,
                Email = $"{id:N}@x.y",
                IsAdmin = isAdmin,
                TokensInvalidatedAt = invalidatedAt,
                IsServiceAccount = serviceAccount,
            });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = id, Role = OrganizationRole.Owner });
            await db.SaveChangesAsync();
            return id;
        }

        public void Dispose() => Services.Dispose();
    }

    private static ClaimsPrincipal Token(string sub, bool platformAdmin, DateTime? iat = null, params (string, string)[] extra)
    {
        List<Claim> claims = [new("sub", sub), new("org", Org.ToString()), new("orgRole", "Owner")];
        if (platformAdmin) claims.Add(new Claim("platformAdmin", "true"));
        claims.Add(new Claim("iat", new DateTimeOffset(iat ?? DateTime.UtcNow).ToUnixTimeSeconds().ToString()));
        claims.AddRange(extra.Select(e => new Claim(e.Item1, e.Item2)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    /// <summary>Runs the real hook; returns the principal and whether authentication failed (401).</summary>
    private static async Task<(ClaimsPrincipal Principal, bool Failed)> Authenticate(Env env, ClaimsPrincipal token)
    {
        using IServiceScope scope = env.Services.CreateScope();
        DefaultHttpContext http = new() { RequestServices = scope.ServiceProvider };
        TokenValidatedContext ctx = new(
            http, new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)), new JwtBearerOptions())
        {
            Principal = token,
        };
        await RealEvents().TokenValidated(ctx);
        return (token, ctx.Result?.Failure is not null);
    }

    private static ControllerContext Ctx(ClaimsPrincipal p) => new() { HttpContext = new DefaultHttpContext { User = p } };

    private static IActionResult AdminStatus(ClaimsPrincipal p) =>
        new AdminController(Mock.Of<ISkillCorpusService>()) { ControllerContext = Ctx(p) }.Status();

    private static async Task<bool> CalibrationForbidden(ClaimsPrincipal p)
    {
        var service = new Mock<IDecisionCalibrationService>();
        DecisionCalibrationController c = new(service.Object) { ControllerContext = Ctx(p) };
        var result = await c.Get(null, null, null, null, CancellationToken.None);
        return result.Result is ForbidResult;
    }

    [Fact]
    public async Task Demoted_admin_with_a_still_valid_token_gets_403_on_admin_status_and_calibration()
    {
        using Env env = new();
        Guid user = await env.SeedUser(isAdmin: false);
        (ClaimsPrincipal p, bool failed) = await Authenticate(env, Token(user.ToString(), platformAdmin: true));

        failed.Should().BeFalse();
        AdminStatus(p).Should().BeOfType<ForbidResult>();
        (await CalibrationForbidden(p)).Should().BeTrue();
    }

    [Fact]
    public async Task Token_issued_before_tokens_invalidated_at_is_rejected_401()
    {
        using Env env = new();
        DateTime cutoff = DateTime.UtcNow;
        Guid user = await env.SeedUser(isAdmin: true, invalidatedAt: cutoff);
        (_, bool failed) = await Authenticate(env, Token(user.ToString(), platformAdmin: true, iat: cutoff.AddHours(-1)));
        failed.Should().BeTrue();
    }

    [Fact]
    public async Task Deleted_user_is_rejected_401()
    {
        using Env env = new();
        (_, bool failed) = await Authenticate(env, Token(Guid.NewGuid().ToString(), platformAdmin: true));
        failed.Should().BeTrue();
    }

    [Fact]
    public async Task Real_admin_passes_admin_status_and_calibration()
    {
        using Env env = new();
        Guid user = await env.SeedUser(isAdmin: true);
        (ClaimsPrincipal p, bool failed) = await Authenticate(env, Token(user.ToString(), platformAdmin: true));

        failed.Should().BeFalse();
        AdminStatus(p).Should().BeOfType<OkObjectResult>();
        (await CalibrationForbidden(p)).Should().BeFalse();
    }

    [Fact]
    public async Task Mcp_assistant_admin_token_of_a_real_admin_still_reaches_calibration()
    {
        using Env env = new();
        Guid user = await env.SeedUser(isAdmin: true);
        (ClaimsPrincipal p, bool failed) = await Authenticate(
            env, Token(user.ToString(), platformAdmin: true, extra: ("scope", "assistant.tools assistant.admin")));
        failed.Should().BeFalse();
        (await CalibrationForbidden(p)).Should().BeFalse();
    }

    [Fact]
    public async Task Engine_on_behalf_of_token_for_a_service_account_still_authenticates_without_admin()
    {
        using Env env = new();
        Guid svc = await env.SeedUser(isAdmin: false, serviceAccount: true);
        (ClaimsPrincipal p, bool failed) = await Authenticate(
            env, Token(svc.ToString(), platformAdmin: false, extra: ("act", "workflow-engine")));
        failed.Should().BeFalse();
        AuthClaims.ReadPlatformAdmin(p).Should().BeFalse();
    }

    [Fact]
    public async Task Runner_device_token_still_authenticates_but_never_as_admin()
    {
        using Env env = new();
        (ClaimsPrincipal p, bool failed) = await Authenticate(
            env, Token($"device:{Guid.NewGuid()}", platformAdmin: true, extra: ("kind", "runner")));
        failed.Should().BeFalse();
        AdminStatus(p).Should().BeOfType<ForbidResult>();
    }
}
