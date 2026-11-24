using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Services.Auth;

/// <summary>
/// Mints the only token the WorkflowEngine accepts inbound when <c>EngineJwt:SigningKey</c> is set: HS256
/// with the engine key, issuer <c>reelbolt-control</c>, audience <c>reelbolt-engine-admin</c>, 60 seconds.
/// It carries <c>sub</c>, <c>platformAdmin</c> and, for a scoped caller (an MCP token), the caller's
/// <c>scope</c> so the engine's own scope guard still applies. Disabled when the engine key is not set.
/// </summary>
public sealed class EngineAdminTokenIssuer
{
    /// <summary>Scope value written for a scoped caller that holds no scope: it stays scoped and matches nothing.</summary>
    public const string NoScope = "-";

    private readonly EngineJwtSettings? _settings;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new();

    public EngineAdminTokenIssuer(IConfiguration configuration, TimeProvider? time = null)
    {
        _settings = EngineJwtSettings.Read(configuration);
        _time = time ?? TimeProvider.System;
    }

    public bool Enabled => _settings is not null;

    /// <param name="scopes">Null for an unscoped caller (a session); the caller's scopes otherwise.</param>
    public string Issue(Guid userId, bool platformAdmin, IReadOnlyCollection<string>? scopes)
    {
        if (_settings is null)
            throw new InvalidOperationException($"{EngineJwtSettings.SigningKeyKey} is not set.");

        DateTime now = _time.GetUtcNow().UtcDateTime;
        Dictionary<string, object> claims = new()
        {
            ["sub"] = userId.ToString(),
            [AuthClaims.PlatformAdmin] = platformAdmin ? "true" : "false"
        };
        if (scopes is not null)
            claims[AuthClaims.Scope] = scopes.Count == 0 ? NoScope : string.Join(' ', scopes);

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = EngineJwtSettings.AdminIssuer,
            Audience = EngineJwtSettings.AdminAudience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + EngineJwtSettings.AdminLifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(_settings.SecurityKey, SecurityAlgorithms.HmacSha256)
        });
    }
}

/// <summary>The <c>Authorization</c> header for one Inference API call to the WorkflowEngine, for the current caller.</summary>
public interface IEngineCallCredential
{
    /// <summary>Null when there is no caller the engine may be called for.</summary>
    Task<AuthenticationHeaderValue?> ForCurrentCallerAsync(CancellationToken ct);
}

/// <summary>
/// Without an engine key it forwards the caller's own bearer token, as before. With one it mints an admin
/// token for the caller: <c>platformAdmin</c> is true only when the caller's validated principal says so
/// AND <c>application_users.is_admin</c> still agrees. It never mints for an API key or for an engine token,
/// so this service cannot be used to widen either.
/// </summary>
public sealed class EngineCallCredential(
    IHttpContextAccessor http,
    EngineAdminTokenIssuer issuer,
    MembershipValidator memberships,
    InferenceApiDbContext db) : IEngineCallCredential
{
    public async Task<AuthenticationHeaderValue?> ForCurrentCallerAsync(CancellationToken ct)
    {
        HttpContext? context = http.HttpContext;
        if (context is null)
            return null;

        if (!issuer.Enabled)
        {
            string? authorization = context.Request.Headers.Authorization.ToString();
            return !string.IsNullOrWhiteSpace(authorization)
                && AuthenticationHeaderValue.TryParse(authorization, out AuthenticationHeaderValue? header)
                    ? header
                    : null;
        }

        ClaimsPrincipal user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || McpScopeGuard.IsApiKey(user)
            || EngineTokenAuthentication.IsEnginePrincipal(context)
            || EngineTokenAuthentication.CarriesEngineActor(user))
            return null;

        string? sub = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out Guid userId))
            return null;

        MembershipValidator.Membership membership =
            await memberships.LookupAsync(db, userId, AuthClaims.ReadOrganizationId(user), ct);
        bool platformAdmin = AuthClaims.ReadPlatformAdmin(user)
            && membership is { UserExists: true, IsPlatformAdmin: true, IsServiceAccount: false };
        IReadOnlyCollection<string>? scopes = McpScopeGuard.IsScoped(user) ? McpScopeGuard.ReadScopes(user) : null;
        return new AuthenticationHeaderValue("Bearer", issuer.Issue(userId, platformAdmin, scopes));
    }
}
