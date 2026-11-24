using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Auth;

/// <summary>
/// Mints a short-lived (default 5 min) HS256 JWT that lets the WorkflowEngine call the Inference API
/// AS the user who started an execution. Same claim shape (<c>sub</c>, <c>email</c>, <c>org</c>,
/// <c>orgRole</c>) the Go API issues, plus <c>act=workflow-engine</c> and <c>executionId</c> markers.
/// Signed with the engine key under the engine issuer and audience when <c>EngineJwt:SigningKey</c> is set
/// (<see cref="EngineJwtSettings"/>); otherwise with the session issuer, audience and signing key. The caller supplies the execution's organization and the initiator's REAL
/// current role in it. The token deliberately carries NO <c>platformAdmin</c> (and no <c>isAdmin</c> alias):
/// an engine call acts with at most the initiator's organization authority, never platform authority.
/// </summary>
public sealed class OnBehalfOfTokenIssuer
{
    public const string ActorClaim = "act";
    public const string ActorValue = "workflow-engine";
    public const string ExecutionIdClaim = "executionId";
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private readonly string _issuer;
    private readonly string _audience;
    private readonly byte[] _key;
    private readonly string _keyName;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new();

    public OnBehalfOfTokenIssuer(IConfiguration configuration, TimeProvider? time = null)
    {
        // With the engine key set, the token is signed with it under the engine's own issuer and audience,
        // so it is not a session token and the session key is not needed (EngineJwtSettings).
        if (EngineJwtSettings.Read(configuration) is { } engine)
        {
            _issuer = engine.Issuer;
            _audience = engine.Audience;
            _key = engine.Key;
            _keyName = EngineJwtSettings.SigningKeyKey;
        }
        else
        {
            _issuer = configuration["Jwt:Issuer"] ?? string.Empty;
            _audience = configuration["Jwt:Audience"] ?? string.Empty;
            _key = Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"] ?? string.Empty);
            _keyName = "Jwt:SigningKey";
        }
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Mints the token. <paramref name="apiKeyId"/> is the key behind a key-started run: it lets the
    /// Inference API keep the key's project restriction on engine callbacks (S-M5). Null for a run a person
    /// started. The token still carries NO <c>scope</c> claim: an engine callback reaches dashboard routes,
    /// and the scope guard would refuse a scoped principal there.
    /// </summary>
    public string Issue(
        Guid userId, string email, Guid organizationId, OrganizationRole orgRole, Guid executionId,
        TimeSpan? lifetime = null, Guid? apiKeyId = null)
    {
        if (_key.Length < 32)
            throw new InvalidOperationException($"{_keyName} must be at least 32 bytes to mint on-behalf-of tokens.");

        DateTime now = _time.GetUtcNow().UtcDateTime;
        Dictionary<string, object> claims = new()
        {
            ["sub"] = userId.ToString(),
            ["email"] = email,
            [AuthClaims.Organization] = organizationId.ToString(),
            [AuthClaims.OrgRole] = orgRole.ToString(),
            [ActorClaim] = ActorValue,
            [ExecutionIdClaim] = executionId.ToString()
        };
        if (apiKeyId is { } keyId)
            claims[AuthClaims.ApiKeyId] = keyId.ToString();

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = _issuer,
            Audience = _audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + (lifetime ?? DefaultLifetime),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256)
        };
        return _handler.CreateToken(descriptor);
    }
}
