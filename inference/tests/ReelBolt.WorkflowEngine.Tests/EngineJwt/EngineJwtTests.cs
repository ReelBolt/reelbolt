#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DataProtection;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using Xunit;
using InferenceRoutes = ReelBolt.Inference.Api.McpScopeRoutes;

namespace ReelBolt.WorkflowEngine.Tests.EngineJwt;

/// <summary>Shared fixtures: obviously fake keys, token minting and the two services' real auth registrations.</summary>
internal static class EngineJwtFixture
{
    public const string SessionKey = "fake-session-signing-key-0123456789abcdef";
    public const string EngineKey = "fake-engine-signing-key-0123456789abcdef!";
    public const string SessionIssuer = "reelbolt-api";
    public const string SessionAudience = "reelbolt-inference";
    public static readonly Guid Org = Guid.Parse("5c1f0000-0000-4000-8000-0000000000e1");

    public static IConfiguration Config(bool engineKey, bool sessionKey = true, params (string Key, string? Value)[] extra)
    {
        Dictionary<string, string?> values = new()
        {
            ["Jwt:Issuer"] = SessionIssuer,
            ["Jwt:Audience"] = SessionAudience
        };
        if (sessionKey) values["Jwt:SigningKey"] = SessionKey;
        if (engineKey) values[EngineJwtSettings.SigningKeyKey] = EngineKey;
        foreach ((string key, string? value) in extra) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public static string Mint(string key, string issuer, string audience, params (string Type, object Value)[] claims) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddSeconds(-5),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = claims.ToDictionary(c => c.Type, c => c.Value),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        });

    public static string SessionToken(Guid user, bool platformAdmin = false, bool withEngineActor = false, string? key = null)
    {
        List<(string, object)> claims =
        [
            ("sub", user.ToString()), ("email", "u@x.y"), ("org", Org.ToString()), ("orgRole", "Member"),
            ("platformAdmin", platformAdmin ? "true" : "false")
        ];
        if (withEngineActor) claims.Add((OnBehalfOfTokenIssuer.ActorClaim, OnBehalfOfTokenIssuer.ActorValue));
        return Mint(key ?? SessionKey, SessionIssuer, SessionAudience, claims.ToArray());
    }

    public static async Task<TokenValidationResult> Validate(string token, string key, string issuer, string audience) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        });

    /// <summary>The Inference API's real authentication registration over an in-memory database.</summary>
    public static ServiceProvider InferenceAuth(IConfiguration configuration, string dbName)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<MembershipValidator>(sp =>
            new MembershipValidator(sp.GetRequiredService<IMemoryCache>(), NullLogger<MembershipValidator>.Instance));
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddInferenceApiAuthentication(configuration);
        return services.BuildServiceProvider();
    }

    public static ServiceProvider EngineAuth(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddLogging();
        // The engine's OnTokenValidated hook re-reads application_users (shared TokenMembershipValidator).
        string dbName = Guid.NewGuid().ToString();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddWorkflowEngineAuthentication(configuration);
        return services.BuildServiceProvider();
    }

    public static async Task<Guid> SeedEngineUserAsync(ServiceProvider sp, bool isAdmin)
    {
        using IServiceScope scope = sp.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        Guid user = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = $"{user:N}@x.y", IsAdmin = isAdmin });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = user, Role = OrganizationRole.Member });
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Guid> SeedUserAsync(ServiceProvider sp, bool isAdmin = false, bool serviceAccount = false)
    {
        using IServiceScope scope = sp.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        Guid user = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "u@x.y", IsAdmin = isAdmin, IsServiceAccount = serviceAccount });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = user, Role = OrganizationRole.Member });
        await db.SaveChangesAsync();
        return user;
    }

    public static DefaultHttpContext Request(IServiceProvider requestServices, string? token, string method = "GET", string path = "/")
    {
        DefaultHttpContext http = new() { RequestServices = requestServices };
        http.Request.Method = method;
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        if (token is not null) http.Request.Headers.Authorization = "Bearer " + token;
        return http;
    }

    public static async Task<(AuthenticateResult Result, DefaultHttpContext Http)> AuthenticateAsync(ServiceProvider sp, string token)
    {
        using IServiceScope scope = sp.CreateScope();
        DefaultHttpContext http = Request(scope.ServiceProvider, token);
        AuthenticateResult result = await scope.ServiceProvider.GetRequiredService<IAuthenticationService>().AuthenticateAsync(http, null);
        return (result, http);
    }

    public static string RepoInferenceDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ReelBolt.sln"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the .sln must be above the test assembly");
        return dir!;
    }
}

public class EngineJwtSettingsTests
{
    [Fact]
    public void Unset_or_blank_key_turns_the_feature_off()
    {
        EngineJwtSettings.Read(EngineJwtFixture.Config(engineKey: false)).Should().BeNull();
        EngineJwtSettings.Read(EngineJwtFixture.Config(false, true, (EngineJwtSettings.SigningKeyKey, "  "))).Should().BeNull();
    }

    [Fact]
    public void Defaults_are_the_engine_issuer_and_audience_and_the_session_key_is_not_needed()
    {
        EngineJwtSettings settings = EngineJwtSettings.Read(EngineJwtFixture.Config(engineKey: true, sessionKey: false))!;
        settings.Issuer.Should().Be("reelbolt-engine");
        settings.Audience.Should().Be("reelbolt-engine-obo");
        EngineJwtSettings.AdminIssuer.Should().Be("reelbolt-control");
        EngineJwtSettings.AdminAudience.Should().Be("reelbolt-engine-admin");
        EngineJwtSettings.AdminLifetime.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Issuer_and_audience_can_be_overridden()
    {
        EngineJwtSettings settings = EngineJwtSettings.Read(EngineJwtFixture.Config(true, true,
            (EngineJwtSettings.IssuerKey, "custom-engine"), (EngineJwtSettings.AudienceKey, "custom-obo")))!;
        settings.Issuer.Should().Be("custom-engine");
        settings.Audience.Should().Be("custom-obo");
    }

    public static IEnumerable<object[]> BadSettings() =>
    [
        [new[] { (EngineJwtSettings.SigningKeyKey, (string?)"too-short-fake-key") }, "at least 32 bytes"],
        [new[] { (EngineJwtSettings.SigningKeyKey, (string?)EngineJwtFixture.SessionKey) }, "must not equal Jwt:SigningKey"],
        [new[] { (EngineJwtSettings.IssuerKey, (string?)EngineJwtFixture.SessionIssuer) }, "must differ from the session token"],
        [new[] { (EngineJwtSettings.IssuerKey, (string?)EngineJwtSettings.AdminIssuer) }, "must differ from the admin token"],
        [new[] { (EngineJwtSettings.AudienceKey, (string?)EngineJwtSettings.AdminAudience) }, "must differ from the admin token"]
    ];

    [Theory]
    [MemberData(nameof(BadSettings))]
    public void Bad_settings_refuse_to_start_in_both_services((string, string?)[] extra, string because)
    {
        IConfiguration configuration = EngineJwtFixture.Config(true, true, extra);
        ((Action)(() => EngineJwtSettings.Read(configuration))).Should().Throw<InvalidOperationException>().WithMessage($"*{because}*");
        ((Action)(() => new ServiceCollection().AddWorkflowEngineAuthentication(configuration)))
            .Should().Throw<InvalidOperationException>();
        ((Action)(() => new ServiceCollection().AddInferenceApiAuthentication(configuration)))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Refusal_messages_never_contain_a_key_value()
    {
        Action act = () => EngineJwtSettings.Read(EngineJwtFixture.Config(true, true,
            (EngineJwtSettings.SigningKeyKey, EngineJwtFixture.SessionKey)));
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain(EngineJwtFixture.SessionKey);
    }
}

public class EngineTokenShapeTests
{
    [Fact]
    public async Task On_behalf_of_token_uses_the_engine_key_issuer_and_audience_without_any_session_key()
    {
        Guid user = Guid.NewGuid(), execution = Guid.NewGuid();
        string token = new OnBehalfOfTokenIssuer(EngineJwtFixture.Config(engineKey: true, sessionKey: false))
            .Issue(user, "a@b.c", EngineJwtFixture.Org, OrganizationRole.Member, execution);

        TokenValidationResult engine = await EngineJwtFixture.Validate(token, EngineJwtFixture.EngineKey, "reelbolt-engine", "reelbolt-engine-obo");
        engine.IsValid.Should().BeTrue();
        JsonWebToken jwt = (JsonWebToken)engine.SecurityToken;
        jwt.GetPayloadValue<string>("act").Should().Be("workflow-engine");
        jwt.GetPayloadValue<string>("sub").Should().Be(user.ToString());
        jwt.GetPayloadValue<string>("executionId").Should().Be(execution.ToString());
        jwt.TryGetPayloadValue("platformAdmin", out string? _).Should().BeFalse();
        (jwt.ValidTo - jwt.ValidFrom).Should().BeCloseTo(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));

        // Not a session token: wrong key, wrong issuer, wrong audience.
        (await EngineJwtFixture.Validate(token, EngineJwtFixture.SessionKey, "reelbolt-engine", "reelbolt-engine-obo")).IsValid.Should().BeFalse();
        (await EngineJwtFixture.Validate(token, EngineJwtFixture.EngineKey, EngineJwtFixture.SessionIssuer, "reelbolt-engine-obo")).IsValid.Should().BeFalse();
        (await EngineJwtFixture.Validate(token, EngineJwtFixture.EngineKey, "reelbolt-engine", EngineJwtFixture.SessionAudience)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Without_the_engine_key_the_on_behalf_of_token_is_the_session_shape_as_before()
    {
        string token = new OnBehalfOfTokenIssuer(EngineJwtFixture.Config(engineKey: false))
            .Issue(Guid.NewGuid(), "a@b.c", EngineJwtFixture.Org, OrganizationRole.Member, Guid.NewGuid());
        (await EngineJwtFixture.Validate(token, EngineJwtFixture.SessionKey, EngineJwtFixture.SessionIssuer, EngineJwtFixture.SessionAudience))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_token_is_sixty_seconds_control_issuer_admin_audience_with_sub_and_platformAdmin()
    {
        Guid user = Guid.NewGuid();
        EngineAdminTokenIssuer issuer = new(EngineJwtFixture.Config(engineKey: true));
        issuer.Enabled.Should().BeTrue();

        TokenValidationResult r = await EngineJwtFixture.Validate(
            issuer.Issue(user, platformAdmin: true, scopes: null), EngineJwtFixture.EngineKey, "reelbolt-control", "reelbolt-engine-admin");
        r.IsValid.Should().BeTrue();
        JsonWebToken jwt = (JsonWebToken)r.SecurityToken;
        jwt.GetPayloadValue<string>("sub").Should().Be(user.ToString());
        jwt.GetPayloadValue<string>("platformAdmin").Should().Be("true");
        jwt.TryGetPayloadValue("scope", out string? _).Should().BeFalse();
        jwt.TryGetPayloadValue("act", out string? _).Should().BeFalse();
        (jwt.ValidTo - jwt.ValidFrom).Should().BeCloseTo(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Admin_token_passes_a_scoped_callers_scope_through_and_keeps_an_empty_scope_set_scoped()
    {
        EngineAdminTokenIssuer issuer = new(EngineJwtFixture.Config(engineKey: true));
        JsonWebToken scoped = (JsonWebToken)(await EngineJwtFixture.Validate(
            issuer.Issue(Guid.NewGuid(), true, ["assistant.admin", "assistant.tools"]),
            EngineJwtFixture.EngineKey, "reelbolt-control", "reelbolt-engine-admin")).SecurityToken;
        scoped.GetPayloadValue<string>("scope").Split(' ').Should().BeEquivalentTo("assistant.admin", "assistant.tools");

        JsonWebToken empty = (JsonWebToken)(await EngineJwtFixture.Validate(
            issuer.Issue(Guid.NewGuid(), true, []), EngineJwtFixture.EngineKey, "reelbolt-control", "reelbolt-engine-admin")).SecurityToken;
        empty.GetPayloadValue<string>("scope").Should().Be(EngineAdminTokenIssuer.NoScope);
    }

    [Fact]
    public void Admin_token_issuer_is_disabled_without_the_engine_key()
    {
        EngineAdminTokenIssuer issuer = new(EngineJwtFixture.Config(engineKey: false));
        issuer.Enabled.Should().BeFalse();
        ((Action)(() => issuer.Issue(Guid.NewGuid(), true, null))).Should().Throw<InvalidOperationException>();
    }
}

/// <summary>The Inference API's schemes, through the real registration and <see cref="IAuthenticationService"/>.</summary>
public class InferenceApiEngineSchemeTests
{
    private static string EngineToken(Guid user, bool actor = true, bool platformAdmin = false, string? key = null,
        string issuer = EngineJwtSettings.DefaultIssuer, string audience = EngineJwtSettings.DefaultAudience)
    {
        List<(string, object)> claims = [("sub", user.ToString()), ("email", "u@x.y"), ("org", EngineJwtFixture.Org.ToString()), ("orgRole", "Member")];
        if (actor) claims.Add((OnBehalfOfTokenIssuer.ActorClaim, OnBehalfOfTokenIssuer.ActorValue));
        if (platformAdmin) claims.Add(("platformAdmin", "true"));
        return EngineJwtFixture.Mint(key ?? EngineJwtFixture.EngineKey, issuer, audience, claims.ToArray());
    }

    private static ServiceProvider Configured() => EngineJwtFixture.InferenceAuth(EngineJwtFixture.Config(engineKey: true), Guid.NewGuid().ToString());

    private static ServiceProvider Unset() => EngineJwtFixture.InferenceAuth(EngineJwtFixture.Config(engineKey: false), Guid.NewGuid().ToString());

    [Fact]
    public async Task The_real_engine_issuer_output_is_accepted_and_marked_as_an_engine_principal()
    {
        using ServiceProvider sp = Configured();
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);
        string token = new OnBehalfOfTokenIssuer(EngineJwtFixture.Config(engineKey: true, sessionKey: false))
            .Issue(user, "u@x.y", EngineJwtFixture.Org, OrganizationRole.Owner, Guid.NewGuid());

        (AuthenticateResult result, DefaultHttpContext http) = await EngineJwtFixture.AuthenticateAsync(sp, token);

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        EngineTokenAuthentication.IsEnginePrincipal(http).Should().BeTrue();
        // Role is database truth (Member), not the token's claim (Owner).
        AuthClaims.ReadOrgRole(result.Principal).Should().Be(OrganizationRole.Member);
    }

    [Fact]
    public async Task An_engine_token_never_yields_platform_admin_even_for_an_admin_user()
    {
        using ServiceProvider sp = Configured();
        Guid admin = await EngineJwtFixture.SeedUserAsync(sp, isAdmin: true);
        (AuthenticateResult result, _) = await EngineJwtFixture.AuthenticateAsync(sp, EngineToken(admin, platformAdmin: true));
        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        AuthClaims.ReadPlatformAdmin(result.Principal).Should().BeFalse();
    }

    [Fact]
    public async Task An_engine_token_without_the_actor_marker_is_refused()
    {
        using ServiceProvider sp = Configured();
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);
        (AuthenticateResult result, DefaultHttpContext http) = await EngineJwtFixture.AuthenticateAsync(sp, EngineToken(user, actor: false));
        result.Succeeded.Should().BeFalse();
        EngineTokenAuthentication.IsEnginePrincipal(http).Should().BeFalse();
    }

    [Fact]
    public async Task A_session_token_carrying_the_actor_marker_is_refused_when_the_engine_key_is_configured()
    {
        using ServiceProvider sp = Configured();
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);
        (await EngineJwtFixture.AuthenticateAsync(sp, EngineJwtFixture.SessionToken(user, withEngineActor: true))).Result.Succeeded.Should().BeFalse();
        // The same user's plain session still works and is not an engine principal.
        (AuthenticateResult plain, DefaultHttpContext http) = await EngineJwtFixture.AuthenticateAsync(sp, EngineJwtFixture.SessionToken(user));
        plain.Succeeded.Should().BeTrue(plain.Failure?.Message);
        EngineTokenAuthentication.IsEnginePrincipal(http).Should().BeFalse();
    }

    [Fact]
    public async Task An_engine_key_token_is_refused_by_the_session_scheme()
    {
        using ServiceProvider sp = Configured();
        Guid user = await EngineJwtFixture.SeedUserAsync(sp, isAdmin: true);
        // Signed with the engine key but shaped as a session: the forgery a stolen engine key would attempt.
        string forged = EngineJwtFixture.SessionToken(user, platformAdmin: true, key: EngineJwtFixture.EngineKey);
        (await EngineJwtFixture.AuthenticateAsync(sp, forged)).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_session_key_token_naming_the_engine_issuer_is_refused_by_the_engine_scheme()
    {
        using ServiceProvider sp = Configured();
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);
        (await EngineJwtFixture.AuthenticateAsync(sp, EngineToken(user, key: EngineJwtFixture.SessionKey))).Result.Succeeded.Should().BeFalse();
        (await EngineJwtFixture.AuthenticateAsync(sp, EngineToken(user, audience: EngineJwtFixture.SessionAudience))).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task The_admin_token_the_api_mints_for_the_engine_is_not_accepted_by_the_api_itself()
    {
        using ServiceProvider sp = Configured();
        Guid admin = await EngineJwtFixture.SeedUserAsync(sp, isAdmin: true);
        string adminToken = new EngineAdminTokenIssuer(EngineJwtFixture.Config(engineKey: true)).Issue(admin, true, null);
        (await EngineJwtFixture.AuthenticateAsync(sp, adminToken)).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Without_the_engine_key_a_session_key_token_with_the_marker_works_as_before_and_engine_tokens_do_not_exist()
    {
        using ServiceProvider sp = Unset();
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);
        string legacy = new OnBehalfOfTokenIssuer(EngineJwtFixture.Config(engineKey: false))
            .Issue(user, "u@x.y", EngineJwtFixture.Org, OrganizationRole.Member, Guid.NewGuid());

        (AuthenticateResult result, DefaultHttpContext http) = await EngineJwtFixture.AuthenticateAsync(sp, legacy);
        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        EngineTokenAuthentication.IsEnginePrincipal(http).Should().BeFalse();

        (await EngineJwtFixture.AuthenticateAsync(sp, EngineToken(user))).Result.Succeeded.Should().BeFalse();
        (await sp.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(EngineTokenAuthentication.Scheme)).Should().BeNull();
    }

    [Fact]
    public async Task Service_account_exemption_is_honoured_only_for_the_engine_scheme_when_the_key_is_configured()
    {
        using ServiceProvider sp = Configured();
        Guid service = await EngineJwtFixture.SeedUserAsync(sp, serviceAccount: true);
        using IServiceScope scope = sp.CreateScope();
        MembershipValidator.Membership membership = await sp.GetRequiredService<MembershipValidator>().LookupAsync(
            scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>(), service, EngineJwtFixture.Org, CancellationToken.None);
        membership.IsServiceAccount.Should().BeTrue();

        ClaimsPrincipal Principal() => new(new ClaimsIdentity(
            [new Claim("sub", service.ToString()), new Claim("org", EngineJwtFixture.Org.ToString()),
             new Claim(OnBehalfOfTokenIssuer.ActorClaim, OnBehalfOfTokenIssuer.ActorValue)], "test"));

        MembershipValidator.Apply(Principal(), membership, EngineJwtFixture.Org, out _, honourEngineActor: true).Should().BeTrue();
        MembershipValidator.Apply(Principal(), membership, EngineJwtFixture.Org, out string? failure, honourEngineActor: false).Should().BeFalse();
        failure.Should().Be("Service accounts cannot use a session token.");
        // The default keeps the old behaviour for callers that do not pass the flag.
        MembershipValidator.Apply(Principal(), membership, EngineJwtFixture.Org, out _).Should().BeTrue();
    }

    private static async Task<(int Status, bool Reached)> ThroughGuard(ServiceProvider sp, string token, string method, string path)
    {
        using IServiceScope scope = sp.CreateScope();
        bool reached = false;
        IApplicationBuilder app = new ApplicationBuilder(scope.ServiceProvider);
        app.UseAuthentication();
        app.UseEngineTokenGuard();
        app.Run(_ => { reached = true; return Task.CompletedTask; });
        DefaultHttpContext http = EngineJwtFixture.Request(scope.ServiceProvider, token, method, path);
        await app.Build()(http);
        return (http.Response.StatusCode, reached);
    }

    [Theory]
    [InlineData("POST", "/api/v1/projects/5c1f0000-0000-4000-8000-0000000000aa/design-system/ingest", true)]
    [InlineData("POST", "/api/v1/projects/5c1f0000-0000-4000-8000-0000000000aa/files/search", true)]
    [InlineData("POST", "/API/V1/Projects/5c1f0000-0000-4000-8000-0000000000aa/Files/Search/", true)]
    [InlineData("GET", "/api/v1/projects/5c1f0000-0000-4000-8000-0000000000aa/files/search", false)]
    [InlineData("POST", "/api/v1/projects/not-a-guid/files/search", false)]
    [InlineData("POST", "/api/v1/projects/5c1f0000-0000-4000-8000-0000000000aa/x/files/search", false)]
    [InlineData("POST", "/api/v1/projects//files/search", false)]
    [InlineData("GET", "/api/v1/projects", false)]
    [InlineData("GET", "/api/v1/projects/5c1f0000-0000-4000-8000-0000000000aa/files", false)]
    [InlineData("GET", "/api/v1/inference-providers", false)]
    [InlineData("GET", "/api/v1/workflow-engine/admin/status", false)]
    [InlineData("POST", "/api/v1/assistant/tool-call", false)]
    public async Task An_engine_token_reaches_only_the_allowlisted_routes_and_gets_403_elsewhere(string method, string path, bool allowed)
    {
        using ServiceProvider sp = Configured();
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);

        (int status, bool reached) = await ThroughGuard(sp, EngineToken(user), method, path);

        reached.Should().Be(allowed);
        status.Should().Be(allowed ? 200 : 403);
    }

    [Fact]
    public async Task The_guard_does_not_touch_sessions_or_anonymous_requests()
    {
        using ServiceProvider sp = Configured();
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);
        (await ThroughGuard(sp, EngineJwtFixture.SessionToken(user), "GET", "/api/v1/projects")).Reached.Should().BeTrue();
        // A token the engine scheme rejects is anonymous, not an engine principal: authorization answers 401 later.
        (await ThroughGuard(sp, EngineToken(user, actor: false), "GET", "/api/v1/projects")).Reached.Should().BeTrue();

        using ServiceProvider unset = Unset();
        Guid other = await EngineJwtFixture.SeedUserAsync(unset);
        string legacy = EngineJwtFixture.SessionToken(other, withEngineActor: true);
        (await ThroughGuard(unset, legacy, "GET", "/api/v1/projects")).Reached.Should().BeTrue("without the engine key nothing is confined");
    }
}

/// <summary>The engine's inbound scheme, through the real registration.</summary>
public class EngineInboundSchemeTests
{
    [Fact]
    public async Task With_the_engine_key_only_the_admin_token_is_accepted_and_no_session_key_is_needed()
    {
        using ServiceProvider sp = EngineJwtFixture.EngineAuth(EngineJwtFixture.Config(engineKey: true, sessionKey: false));
        Guid user = await EngineJwtFixture.SeedEngineUserAsync(sp, isAdmin: true);
        IConfiguration control = EngineJwtFixture.Config(engineKey: true);

        (AuthenticateResult admin, _) = await EngineJwtFixture.AuthenticateAsync(sp, new EngineAdminTokenIssuer(control).Issue(user, true, null));
        admin.Succeeded.Should().BeTrue(admin.Failure?.Message);
        AuthClaims.ReadPlatformAdmin(admin.Principal).Should().BeTrue();

        (AuthenticateResult notAdmin, _) = await EngineJwtFixture.AuthenticateAsync(sp, new EngineAdminTokenIssuer(control).Issue(user, false, null));
        notAdmin.Succeeded.Should().BeTrue();
        AuthClaims.ReadPlatformAdmin(notAdmin.Principal).Should().BeFalse();

        (AuthenticateResult scoped, _) = await EngineJwtFixture.AuthenticateAsync(sp, new EngineAdminTokenIssuer(control).Issue(user, true, ["assistant.tools"]));
        McpScopeGuard.IsScoped(scoped.Principal).Should().BeTrue();
        McpScopeGuard.IsAllowed(scoped.Principal!, "GET", ReelBolt.WorkflowEngine.McpScopeRoutes.DecisionCalibration, ReelBolt.WorkflowEngine.McpScopeRoutes.Allowed)
            .Should().BeFalse("the engine's own scope guard still applies to the passed-through scope");

        // Incident fix: platformAdmin on the admin token survives only while application_users.is_admin agrees,
        // and a token for a user with no row is refused.
        Guid demoted = await EngineJwtFixture.SeedEngineUserAsync(sp, isAdmin: false);
        (AuthenticateResult stale, _) = await EngineJwtFixture.AuthenticateAsync(sp, new EngineAdminTokenIssuer(control).Issue(demoted, true, null));
        stale.Succeeded.Should().BeTrue(stale.Failure?.Message);
        AuthClaims.ReadPlatformAdmin(stale.Principal).Should().BeFalse();
        (await EngineJwtFixture.AuthenticateAsync(sp, new EngineAdminTokenIssuer(control).Issue(Guid.NewGuid(), true, null)))
            .Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task With_the_engine_key_a_session_token_and_the_engines_own_token_are_refused()
    {
        using ServiceProvider sp = EngineJwtFixture.EngineAuth(EngineJwtFixture.Config(engineKey: true));
        Guid user = Guid.NewGuid();

        (await EngineJwtFixture.AuthenticateAsync(sp, EngineJwtFixture.SessionToken(user, platformAdmin: true))).Result.Succeeded.Should().BeFalse();
        // Session shape signed with the engine key: wrong issuer and audience for the inbound scheme.
        (await EngineJwtFixture.AuthenticateAsync(sp, EngineJwtFixture.SessionToken(user, platformAdmin: true, key: EngineJwtFixture.EngineKey)))
            .Result.Succeeded.Should().BeFalse();
        // The engine's own on-behalf-of token cannot be replayed at the engine.
        string onBehalfOf = new OnBehalfOfTokenIssuer(EngineJwtFixture.Config(engineKey: true))
            .Issue(user, "u@x.y", EngineJwtFixture.Org, OrganizationRole.Owner, Guid.NewGuid());
        (await EngineJwtFixture.AuthenticateAsync(sp, onBehalfOf)).Result.Succeeded.Should().BeFalse();
        // Admin shape signed with the session key.
        string wrongKey = EngineJwtFixture.Mint(EngineJwtFixture.SessionKey, EngineJwtSettings.AdminIssuer, EngineJwtSettings.AdminAudience,
            ("sub", user.ToString()), ("platformAdmin", "true"));
        (await EngineJwtFixture.AuthenticateAsync(sp, wrongKey)).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Without_the_engine_key_the_engine_accepts_a_session_token_as_before()
    {
        using ServiceProvider sp = EngineJwtFixture.EngineAuth(EngineJwtFixture.Config(engineKey: false));
        Guid user = await EngineJwtFixture.SeedEngineUserAsync(sp, isAdmin: true);
        (AuthenticateResult result, _) = await EngineJwtFixture.AuthenticateAsync(sp, EngineJwtFixture.SessionToken(user, platformAdmin: true));
        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        AuthClaims.ReadPlatformAdmin(result.Principal).Should().BeTrue();
    }
}

public class WorkflowEngineProxyTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = "{\"service\":\"workflow-engine\",\"status\":\"running\"}";
        public bool Throw;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("fake: engine down");
            Request = request;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? Name;

        public HttpClient CreateClient(string name)
        {
            Name = name;
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://engine.test") };
        }
    }

    private sealed class FixedCredential(AuthenticationHeaderValue? header) : IEngineCallCredential
    {
        public int Calls;

        public Task<AuthenticationHeaderValue?> ForCurrentCallerAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(header);
        }
    }

    private static ClaimsPrincipal User(bool platformAdmin, string? scope = null, Guid? id = null)
    {
        List<Claim> claims = [new("sub", (id ?? Guid.NewGuid()).ToString()), new(AuthClaims.PlatformAdmin, platformAdmin ? "true" : "false")];
        if (scope is not null) claims.Add(new Claim(AuthClaims.Scope, scope));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static (WorkflowEngineProxyController Controller, DefaultHttpContext Http) Controller(
        Capture capture, FixedCredential credential, ClaimsPrincipal user, string path, string query = "")
    {
        DefaultHttpContext http = new() { User = user };
        http.Request.Method = "GET";
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        http.Response.Body = new MemoryStream();
        WorkflowEngineProxyController controller = new(new Factory(capture), credential)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return (controller, http);
    }

    [Fact]
    public async Task A_non_admin_gets_403_and_the_engine_is_never_called()
    {
        Capture capture = new();
        FixedCredential credential = new(new AuthenticationHeaderValue("Bearer", "fake"));
        (WorkflowEngineProxyController controller, _) = Controller(capture, credential, User(platformAdmin: false), WorkflowEngineProxyController.AdminStatusPath);

        (await controller.Forward(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        capture.Request.Should().BeNull();
        credential.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_admin_request_is_forwarded_with_the_minted_credential_and_the_engines_answer_is_returned_unchanged()
    {
        Capture capture = new() { Status = HttpStatusCode.BadRequest, Body = "{\"error\":\"acceptAt and minMargin must both be within [0, 1].\"}" };
        FixedCredential credential = new(new AuthenticationHeaderValue("Bearer", "fake-minted"));
        (WorkflowEngineProxyController controller, DefaultHttpContext http) = Controller(
            capture, credential, User(platformAdmin: true), DecisionCalibrationClient.CalibrationPath, "?acceptAt=7&site=a%20b");
        http.Request.Headers.Authorization = "Bearer fake-caller-session";

        IActionResult result = await controller.Forward(CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        capture.Request!.Method.Should().Be(HttpMethod.Get);
        capture.Request.RequestUri!.PathAndQuery.Should().Be("/api/v1/workflow-engine/decision-calibration?acceptAt=7&site=a%20b");
        capture.Request.Headers.Authorization!.Parameter.Should().Be("fake-minted", "the caller's own header is not what reaches the engine");
        http.Response.StatusCode.Should().Be(400);
        http.Response.ContentType.Should().StartWith("application/json");
        http.Response.Body.Position = 0;
        (await new StreamReader(http.Response.Body).ReadToEndAsync()).Should().Be(capture.Body);
    }

    [Fact]
    public async Task No_credential_is_403_and_an_unreachable_engine_is_502()
    {
        (WorkflowEngineProxyController none, _) = Controller(new Capture(), new FixedCredential(null), User(true), WorkflowEngineProxyController.SkillsStatusPath);
        (await none.Forward(CancellationToken.None)).Should().BeOfType<ForbidResult>();

        (WorkflowEngineProxyController down, _) = Controller(
            new Capture { Throw = true }, new FixedCredential(new AuthenticationHeaderValue("Bearer", "fake")), User(true), WorkflowEngineProxyController.SkillsStatusPath);
        (await down.Forward(CancellationToken.None)).Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(502);
    }

    [Fact]
    public void The_proxy_serves_exactly_the_engines_three_authenticated_routes_and_requires_authentication()
    {
        string[] proxied = typeof(WorkflowEngineProxyController).GetMethod(nameof(WorkflowEngineProxyController.Forward))!
            .GetCustomAttributes<HttpGetAttribute>().Select(a => a.Template!).Order().ToArray();

        List<string> engine = [];
        foreach (Type controller in typeof(ReelBolt.WorkflowEngine.Controllers.AdminController).Assembly.GetTypes()
                     .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            if (controller.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() is not null) continue;
            string prefix = controller.GetCustomAttribute<RouteAttribute>()!.Template.Replace("[controller]", controller.Name.Replace("Controller", "").ToLowerInvariant());
            foreach (HttpMethodAttribute verb in controller.GetMethods().SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>()))
            {
                verb.HttpMethods.Should().BeEquivalentTo(["GET"], "the proxy forwards GET only; a new engine verb needs a decision");
                engine.Add(verb.Template is null ? "/" + prefix
                    : verb.Template.StartsWith('/') ? verb.Template : "/" + prefix + "/" + verb.Template);
            }
        }

        proxied.Should().BeEquivalentTo(engine.Order(), "every authenticated engine route is reachable only through the proxy");
        typeof(WorkflowEngineProxyController).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void An_mcp_token_reaches_only_the_calibration_read_and_only_with_the_admin_scope()
    {
        McpScopeGuard.IsAllowed(User(true, "assistant.admin"), "GET", DecisionCalibrationClient.CalibrationPath, InferenceRoutes.Allowed).Should().BeTrue();
        McpScopeGuard.IsAllowed(User(true, "assistant.tools"), "GET", DecisionCalibrationClient.CalibrationPath, InferenceRoutes.Allowed).Should().BeFalse();
        McpScopeGuard.IsAllowed(User(true, "assistant.admin"), "GET", WorkflowEngineProxyController.AdminStatusPath, InferenceRoutes.Allowed).Should().BeFalse();
        McpScopeGuard.IsAllowed(User(true, "assistant.admin"), "GET", WorkflowEngineProxyController.SkillsStatusPath, InferenceRoutes.Allowed).Should().BeFalse();
        // Same rule as the engine's own allowlist.
        InferenceRoutes.Allowed.Where(r => r.Path == DecisionCalibrationClient.CalibrationPath)
            .Should().ContainSingle().Which.Should().BeEquivalentTo(ReelBolt.WorkflowEngine.McpScopeRoutes.Allowed.Single());
    }

    [Fact]
    public void The_engine_token_guard_is_wired_right_after_authentication_in_the_inference_api()
    {
        string src = File.ReadAllText(Path.Combine(EngineJwtFixture.RepoInferenceDir(), "src", "ReelBolt.Inference.Api", "Program.cs"));
        int authn = src.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);
        int guard = src.IndexOf("app.UseEngineTokenGuard()", StringComparison.Ordinal);
        int authz = src.IndexOf("app.UseAuthorization()", StringComparison.Ordinal);
        authn.Should().BeGreaterThanOrEqualTo(0);
        guard.Should().BeGreaterThan(authn);
        authz.Should().BeGreaterThan(guard);
    }
}

public class EngineCallCredentialTests
{
    private static async Task<(AuthenticationHeaderValue? Header, Guid User)> Run(
        bool engineKey, bool dbAdmin, bool claimAdmin, string? scope = null, bool apiKey = false, bool enginePrincipal = false,
        string? callerHeader = "Bearer fake-caller-session")
    {
        IConfiguration configuration = EngineJwtFixture.Config(engineKey);
        using ServiceProvider sp = EngineJwtFixture.InferenceAuth(configuration, Guid.NewGuid().ToString());
        Guid user = await EngineJwtFixture.SeedUserAsync(sp, isAdmin: dbAdmin);
        using IServiceScope services = sp.CreateScope();

        List<Claim> claims = [new("sub", user.ToString()), new("org", EngineJwtFixture.Org.ToString()), new(AuthClaims.PlatformAdmin, claimAdmin ? "true" : "false")];
        if (scope is not null) claims.Add(new Claim(AuthClaims.Scope, scope));
        if (apiKey) claims.Add(new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString()));
        DefaultHttpContext http = new() { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        if (callerHeader is not null) http.Request.Headers.Authorization = callerHeader;
        if (enginePrincipal) EngineTokenAuthentication.MarkEnginePrincipal(http);

        EngineCallCredential credential = new(
            new HttpContextAccessor { HttpContext = http }, new EngineAdminTokenIssuer(configuration),
            sp.GetRequiredService<MembershipValidator>(), services.ServiceProvider.GetRequiredService<InferenceApiDbContext>());
        return (await credential.ForCurrentCallerAsync(CancellationToken.None), user);
    }

    private static async Task<JsonWebToken> Admin(AuthenticationHeaderValue? header)
    {
        header.Should().NotBeNull();
        header!.Scheme.Should().Be("Bearer");
        TokenValidationResult r = await EngineJwtFixture.Validate(header.Parameter!, EngineJwtFixture.EngineKey, "reelbolt-control", "reelbolt-engine-admin");
        r.IsValid.Should().BeTrue();
        return (JsonWebToken)r.SecurityToken;
    }

    [Fact]
    public async Task Without_the_engine_key_the_callers_own_header_is_forwarded_as_before()
    {
        (AuthenticationHeaderValue? header, _) = await Run(engineKey: false, dbAdmin: false, claimAdmin: false);
        header!.ToString().Should().Be("Bearer fake-caller-session");
        (await Run(engineKey: false, dbAdmin: true, claimAdmin: true, callerHeader: null)).Header.Should().BeNull();
    }

    [Fact]
    public async Task With_the_engine_key_a_token_is_minted_and_platformAdmin_needs_both_the_principal_and_the_database()
    {
        (AuthenticationHeaderValue? header, Guid user) = await Run(engineKey: true, dbAdmin: true, claimAdmin: true);
        header!.Parameter.Should().NotBe("fake-caller-session");
        JsonWebToken admin = await Admin(header);
        admin.GetPayloadValue<string>("sub").Should().Be(user.ToString());
        admin.GetPayloadValue<string>("platformAdmin").Should().Be("true");

        (await Admin((await Run(true, dbAdmin: false, claimAdmin: true)).Header)).GetPayloadValue<string>("platformAdmin").Should().Be("false");
        (await Admin((await Run(true, dbAdmin: true, claimAdmin: false)).Header)).GetPayloadValue<string>("platformAdmin").Should().Be("false");
    }

    [Fact]
    public async Task A_scoped_callers_scope_is_passed_through()
    {
        JsonWebToken token = await Admin((await Run(true, dbAdmin: true, claimAdmin: true, scope: "assistant.admin")).Header);
        token.GetPayloadValue<string>("scope").Should().Be("assistant.admin");
    }

    [Fact]
    public async Task Nothing_is_minted_for_an_api_key_or_for_an_engine_principal()
    {
        (await Run(true, dbAdmin: true, claimAdmin: true, scope: "runs.read", apiKey: true)).Header.Should().BeNull();
        (await Run(true, dbAdmin: true, claimAdmin: true, enginePrincipal: true)).Header.Should().BeNull();
    }

    [Fact]
    public async Task The_calibration_client_sends_the_credential_not_the_callers_header()
    {
        HttpRequestMessage? seen = null;
        Mock handler = new(r => seen = r);
        DecisionCalibrationClient client = new(new SingleClientFactory(handler), new Fixed(new AuthenticationHeaderValue("Bearer", "fake-minted")));

        (string? json, string? error) = await client.GetAsync("site-a", 0.5, null, CancellationToken.None);

        error.Should().BeNull();
        json.Should().NotBeNull();
        seen!.Headers.Authorization!.Parameter.Should().Be("fake-minted");
        seen.RequestUri!.PathAndQuery.Should().Be("/api/v1/workflow-engine/decision-calibration?acceptAt=0.5&site=site-a");

        DecisionCalibrationClient none = new(new SingleClientFactory(handler), new Fixed(null));
        (await none.GetAsync(null, null, null, CancellationToken.None)).Error.Should().Be("no caller credential available to read calibration data");
    }

    private sealed class Fixed(AuthenticationHeaderValue? header) : IEngineCallCredential
    {
        public Task<AuthenticationHeaderValue?> ForCurrentCallerAsync(CancellationToken ct) => Task.FromResult(header);
    }

    private sealed class Mock(Action<HttpRequestMessage> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"providers\":[]}") });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://engine.test") };
    }
}

/// <summary>Ties the engine-token allowlist to the engine's <c>"InferenceApi"</c> client call sites and to the real routes.</summary>
public class EngineTokenRouteDriftTests
{
    private static readonly Regex ProjectRouteLiteral = new("\"[^\"\\n]*api/v1/[^\"\\n]*\"", RegexOptions.Compiled);

    [Fact]
    public void Every_inference_api_call_site_in_the_engine_targets_an_allowlisted_route_and_every_entry_is_used()
    {
        string engineDir = Path.Combine(EngineJwtFixture.RepoInferenceDir(), "src", "ReelBolt.WorkflowEngine");
        string sep = Path.DirectorySeparatorChar.ToString();
        var callSites = Directory.EnumerateFiles(engineDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .Where(f => Regex.IsMatch(f.Text, @"CreateClient\(\s*([\w.]*InferenceApiClient\.Name|""InferenceApi"")\s*\)"))
            .ToList();
        callSites.Should().NotBeEmpty();

        string[] suffixes = EngineTokenAuthentication.Allowed.Select(r => r.Suffix).ToArray();
        HashSet<string> used = [];
        foreach ((string file, string text) in callSites)
        {
            string[] literals = ProjectRouteLiteral.Matches(text).Select(m => m.Value.Trim('"')).ToArray();
            literals.Should().NotBeEmpty($"{Path.GetFileName(file)} uses the InferenceApi client, so its route must be visible to this test");
            foreach (string literal in literals)
            {
                string? suffix = suffixes.FirstOrDefault(s => literal.EndsWith(s, StringComparison.Ordinal));
                suffix.Should().NotBeNull(
                    $"{Path.GetFileName(file)} calls '{literal}' through the InferenceApi client: add it to EngineTokenAuthentication.Allowed or the call fails with 403 when the engine key is set");
                literal.TrimStart('/').Should().StartWith("api/v1/projects/");
                used.Add(suffix!);
            }
        }

        used.Should().BeEquivalentTo(suffixes, "an allowlist entry no call site uses is authority nobody needs");
    }

    [Theory]
    [InlineData(typeof(DesignSystemsController), "ingest", EngineTokenAuthentication.DesignSystemIngestSuffix)]
    [InlineData(typeof(ProjectFilesController), "search", EngineTokenAuthentication.FileSearchSuffix)]
    public void Each_allowlist_entry_matches_a_real_post_route(Type controller, string template, string suffix)
    {
        string prefix = controller.GetCustomAttribute<RouteAttribute>()!.Template;
        controller.GetMethods().Any(m => m.GetCustomAttribute<HttpPostAttribute>()?.Template == template).Should().BeTrue();
        string route = "/" + prefix.Replace("{projectId:guid}", Guid.NewGuid().ToString()) + "/" + template;
        route.Should().EndWith(suffix);
        EngineTokenAuthentication.IsAllowed("POST", route).Should().BeTrue();
        EngineTokenAuthentication.Allowed.Should().OnlyContain(r => r.Method == "POST").And.HaveCount(2);
    }
}

/// <summary>File-system key ring: the engine reads what the Inference API wrote and never writes.</summary>
public sealed class ReadOnlyFileKeyRingTests : IDisposable
{
    private readonly string _keysDir = Path.Combine(Path.GetTempPath(), "rb-dp-ro-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_keysDir)) Directory.Delete(_keysDir, recursive: true);
    }

    private ServiceProvider Build(bool engine, bool engineKey = true)
    {
        Dictionary<string, string?> values = new() { ["DataProtection:KeysPath"] = _keysDir };
        if (engineKey)
            values[EngineJwtSettings.SigningKeyKey] = "fake-engine-key-0123456789abcdef01234";
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        ServiceCollection services = new();
        services.AddLogging();
        if (engine)
            services.AddReelBoltDataProtection<WorkflowEngineDbContext>(configuration, readOnlyRing: true);
        else
            services.AddReelBoltDataProtection<InferenceApiDbContext>(configuration, readOnlyRing: false);
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void With_no_ring_the_engine_generates_nothing_and_writes_nothing()
    {
        using ServiceProvider engine = Build(engine: true);

        Action protect = () => engine.GetRequiredService<ISecretProtector>().Protect("fake-secret");

        protect.Should().Throw<Exception>("the engine must not create a key of its own");
        Directory.Exists(_keysDir).Should().BeFalse("the engine does not even create the directory");
    }

    [Fact]
    public void The_engine_reads_the_api_ring_and_leaves_the_directory_untouched()
    {
        string ciphertext;
        using (ServiceProvider api = Build(engine: false))
            ciphertext = api.GetRequiredService<ISecretProtector>().Protect("fake-provider-key");
        string[] before = Directory.GetFiles(_keysDir).Order().ToArray();
        before.Should().ContainSingle();
        DateTime written = File.GetLastWriteTimeUtc(before[0]);

        using ServiceProvider engine = Build(engine: true);
        ISecretProtector protector = engine.GetRequiredService<ISecretProtector>();
        protector.TryUnprotect(ciphertext, out string plaintext).Should().BeTrue();
        plaintext.Should().Be("fake-provider-key");
        // Encrypting with the existing key is fine; it adds no key.
        protector.TryUnprotect(protector.Protect("fake-round-trip"), out string again).Should().BeTrue();
        again.Should().Be("fake-round-trip");

        Directory.GetFiles(_keysDir).Order().Should().Equal(before);
        File.GetLastWriteTimeUtc(before[0]).Should().Be(written);
    }

    [Fact]
    public void Without_the_engine_key_the_engine_keeps_the_writable_ring_it_had_before()
    {
        using ServiceProvider engine = Build(engine: true, engineKey: false);
        ISecretProtector protector = engine.GetRequiredService<ISecretProtector>();

        protector.TryUnprotect(protector.Protect("fake-secret"), out string plaintext).Should().BeTrue();
        plaintext.Should().Be("fake-secret");
        Directory.GetFiles(_keysDir).Should().ContainSingle("single-host generates and persists the ring exactly as before");
    }

    [Fact]
    public void The_read_only_repository_refuses_to_store()
    {
        Action store = () => new ReadOnlyFileKeyRepository(_keysDir).StoreElement(new System.Xml.Linq.XElement("key"), "fake");
        store.Should().Throw<InvalidOperationException>();
        new ReadOnlyFileKeyRepository(_keysDir).GetAllElements().Should().BeEmpty();
        Directory.Exists(_keysDir).Should().BeFalse();
    }
}

/// <summary>Cut-over switch <c>EngineJwt:AcceptLegacyOnBehalfOf</c> and the session-key startup refusals.</summary>
public class EngineJwtCutOverTests
{
    private static ServiceProvider Api(string? acceptLegacy) => EngineJwtFixture.InferenceAuth(
        acceptLegacy is null
            ? EngineJwtFixture.Config(engineKey: true)
            : EngineJwtFixture.Config(true, true, (EngineJwtSettings.AcceptLegacyOnBehalfOfKey, acceptLegacy)),
        Guid.NewGuid().ToString());

    private static async Task<(int Status, bool Reached)> ThroughGuard(ServiceProvider sp, string token, string method, string path)
    {
        using IServiceScope scope = sp.CreateScope();
        bool reached = false;
        IApplicationBuilder app = new ApplicationBuilder(scope.ServiceProvider);
        app.UseAuthentication();
        app.UseEngineTokenGuard();
        app.Run(ctx => { reached = ctx.User.Identity?.IsAuthenticated == true; return Task.CompletedTask; });
        DefaultHttpContext http = EngineJwtFixture.Request(scope.ServiceProvider, token, method, path);
        await app.Build()(http);
        return (http.Response.StatusCode, reached);
    }

    private const string Ingest = "/api/v1/projects/5c1f0000-0000-4000-8000-0000000000aa/design-system/ingest";

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    public async Task Default_is_strict_a_session_key_engine_token_is_refused(string? setting)
    {
        using ServiceProvider sp = Api(setting);
        EngineJwtSettings.Read(setting is null
            ? EngineJwtFixture.Config(engineKey: true)
            : EngineJwtFixture.Config(true, true, (EngineJwtSettings.AcceptLegacyOnBehalfOfKey, setting)))!.AcceptLegacyOnBehalfOf.Should().BeFalse();
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);
        (await EngineJwtFixture.AuthenticateAsync(sp, EngineJwtFixture.SessionToken(user, withEngineActor: true))).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task When_on_a_session_key_engine_token_is_accepted_on_the_two_routes_only_and_counted()
    {
        using ServiceProvider sp = Api("true");
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);
        string legacy = new OnBehalfOfTokenIssuer(EngineJwtFixture.Config(engineKey: false))
            .Issue(user, "u@x.y", EngineJwtFixture.Org, OrganizationRole.Member, Guid.NewGuid());

        long counted = 0;
        using System.Diagnostics.Metrics.MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EngineTokenAuthentication.MeterName && instrument.Name == EngineTokenAuthentication.LegacyCounterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref counted, value));
        listener.Start();

        (AuthenticateResult result, DefaultHttpContext http) = await EngineJwtFixture.AuthenticateAsync(sp, legacy);
        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        EngineTokenAuthentication.IsEnginePrincipal(http).Should().BeTrue("a legacy engine token is confined like an engine token");
        Interlocked.Read(ref counted).Should().BeGreaterThanOrEqualTo(1);

        (await ThroughGuard(sp, legacy, "POST", Ingest)).Should().Be((200, true));
        (await ThroughGuard(sp, legacy, "GET", "/api/v1/projects")).Should().Be((403, false));
        (await ThroughGuard(sp, legacy, "GET", "/api/v1/workflow-engine/admin/status")).Should().Be((403, false));
    }

    [Fact]
    public async Task When_on_plain_sessions_and_real_engine_tokens_are_unaffected_and_forgeries_still_fail()
    {
        using ServiceProvider sp = Api("true");
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);

        (await ThroughGuard(sp, EngineJwtFixture.SessionToken(user), "GET", "/api/v1/projects")).Should().Be((200, true));

        string engine = new OnBehalfOfTokenIssuer(EngineJwtFixture.Config(engineKey: true, sessionKey: false))
            .Issue(user, "u@x.y", EngineJwtFixture.Org, OrganizationRole.Member, Guid.NewGuid());
        (await ThroughGuard(sp, engine, "POST", Ingest)).Should().Be((200, true));
        (await ThroughGuard(sp, engine, "GET", "/api/v1/projects")).Should().Be((403, false));

        // The engine key still cannot make a session, with or without the marker.
        (await EngineJwtFixture.AuthenticateAsync(sp, EngineJwtFixture.SessionToken(user, key: EngineJwtFixture.EngineKey))).Result.Succeeded.Should().BeFalse();
        (await EngineJwtFixture.AuthenticateAsync(sp, EngineJwtFixture.SessionToken(user, withEngineActor: true, key: EngineJwtFixture.EngineKey)))
            .Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task The_switch_does_nothing_without_the_engine_key()
    {
        IConfiguration configuration = EngineJwtFixture.Config(false, true, (EngineJwtSettings.AcceptLegacyOnBehalfOfKey, "true"));
        EngineJwtSettings.Read(configuration).Should().BeNull();
        using ServiceProvider sp = EngineJwtFixture.InferenceAuth(configuration, Guid.NewGuid().ToString());
        Guid user = await EngineJwtFixture.SeedUserAsync(sp);
        // As before the change: accepted and not confined.
        (await ThroughGuard(sp, EngineJwtFixture.SessionToken(user, withEngineActor: true), "GET", "/api/v1/projects")).Should().Be((200, true));
    }

    [Fact]
    public void A_value_that_is_not_a_boolean_refuses_to_start()
    {
        Action act = () => EngineJwtSettings.Read(EngineJwtFixture.Config(true, true, (EngineJwtSettings.AcceptLegacyOnBehalfOfKey, "yes")));
        act.Should().Throw<InvalidOperationException>().WithMessage("*must be true or false*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fake-too-short")]
    public void A_missing_or_short_session_key_refuses_to_start(string? sessionKey)
    {
        (string, string?)[] extra = sessionKey is null ? [] : [("Jwt:SigningKey", sessionKey)];

        // The Inference API always needs the session key, engine key or not.
        foreach (bool engineKey in new[] { false, true })
        {
            IConfiguration configuration = EngineJwtFixture.Config(engineKey, sessionKey: false, extra);
            ((Action)(() => EngineJwtSettings.RequireSigningKeys(configuration, engineKeyReplacesSessionKey: false)))
                .Should().Throw<InvalidOperationException>().WithMessage("*JWT_SIGNING_KEY*");
        }

        // The engine needs it only while it has no engine key.
        ((Action)(() => EngineJwtSettings.RequireSigningKeys(EngineJwtFixture.Config(false, false, extra), engineKeyReplacesSessionKey: true)))
            .Should().Throw<InvalidOperationException>().WithMessage("*JWT_SIGNING_KEY*");
        ((Action)(() => EngineJwtSettings.RequireSigningKeys(EngineJwtFixture.Config(true, false, extra), engineKeyReplacesSessionKey: true)))
            .Should().NotThrow();
        ((Action)(() => EngineJwtSettings.RequireSigningKeys(EngineJwtFixture.Config(engineKey: false), engineKeyReplacesSessionKey: true)))
            .Should().NotThrow();
    }

    [Theory]
    [InlineData("ReelBolt.Inference.Api", "engineKeyReplacesSessionKey: false", "AddInferenceApiServices")]
    [InlineData("ReelBolt.WorkflowEngine", "engineKeyReplacesSessionKey: true", "AddWorkflowEngineServices")]
    public void Each_service_checks_its_signing_keys_before_it_registers_anything(string project, string argument, string registration)
    {
        string src = File.ReadAllText(Path.Combine(EngineJwtFixture.RepoInferenceDir(), "src", project, "Program.cs"));
        int check = src.IndexOf($"EngineJwtSettings.RequireSigningKeys(builder.Configuration, {argument});", StringComparison.Ordinal);
        check.Should().BeGreaterThanOrEqualTo(0);
        src.IndexOf(registration, StringComparison.Ordinal).Should().BeGreaterThan(check);
    }
}
