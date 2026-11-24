using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.ApiKeys;

public static class ApiKeyAuthentication
{
    public const string Scheme = "ApiKey";
    public const string PolicyScheme = "JwtOrApiKey";
    internal const string FailureItem = "ReelBolt.ApiKey.Failure";

    public const string InvalidKey = "invalid_api_key";
    public const string Revoked = "api_key_revoked";
    public const string Expired = "api_key_expired";
    public const string Inactive = "api_key_inactive";
    public const string Disabled = "api_disabled";

    /// <summary>True for <c>Authorization: Bearer rbk_...</c>: such a request is judged by the ApiKey scheme only.</summary>
    public static bool IsApiKeyRequest(HttpRequest request)
    {
        string? header = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)) return false;
        return ApiKeyToken.LooksLikeKey(header.AsSpan(bearer.Length).Trim().ToString());
    }

    internal static string MessageFor(string code) => code switch
    {
        Revoked => "This API key has been revoked.",
        Expired => "This API key has expired.",
        Inactive => "This API key's service account is no longer active in the organization.",
        Disabled => "The public API is not enabled.",
        _ => "The API key is not valid."
    };
}

/// <summary>
/// Authenticates <c>Bearer rbk_...</c> tokens. The principal is the key's own service account at its LIVE role (always Member)
/// (database, not anything stored on the key), is never a platform admin, and carries the key's scopes.
/// A password change does NOT revoke keys: a key is a long-lived credential like a GitHub PAT, revoked on purpose
/// from the dashboard. It survives its creator leaving because it acts as its own service account.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        string token = header.Length > 7 ? header[7..].Trim() : string.Empty;
        IServiceProvider services = Context.RequestServices;

        if (!services.GetRequiredService<IOptions<PublicApiOptions>>().Value.Enabled)
            return Failure(ApiKeyAuthentication.Disabled);

        CancellationToken ct = Context.RequestAborted;
        ApiKeyVerifyResult verified = await services.GetRequiredService<IApiKeyService>().VerifyAsync(token, ct);
        switch (verified.Status)
        {
            case ApiKeyVerifyStatus.Revoked: return Failure(ApiKeyAuthentication.Revoked);
            case ApiKeyVerifyStatus.Expired: return Failure(ApiKeyAuthentication.Expired);
            case ApiKeyVerifyStatus.Ok: break;
            default: return Failure(ApiKeyAuthentication.InvalidKey);
        }

        ReelBolt.Shared.Data.Models.ApiKey key = verified.Key!;
        InferenceApiDbContext db = services.GetRequiredService<InferenceApiDbContext>();
        MembershipValidator validator = services.GetRequiredService<MembershipValidator>();
        // The key acts as its own service account. Same membership lookup the JWT path uses (30 s cache).
        MembershipValidator.Membership membership = await validator.LookupAsync(db, key.ServiceUserId, key.OrganizationId, ct);
        var user = await db.ApplicationUsers.AsNoTracking()
            .Where(u => u.Id == key.ServiceUserId)
            .Select(u => new { u.Email, u.EmailVerifiedAt, u.IsServiceAccount, u.IsAdmin })
            .FirstOrDefaultAsync(ct);
        // M7/M8: an organization delete is a SOFT delete (organizations.deleted_at, set by the Go API), so
        // until AccountPurgeService purges the workspace its keys and service users still exist. Refuse them
        // the moment the org is gone rather than serving it for the whole purge window.
        bool organizationActive = await db.Organizations.AsNoTracking()
            .AnyAsync(o => o.Id == key.OrganizationId && o.DeletedAt == null, ct);
        // Fail closed: row missing, not a service account, membership gone, org soft-deleted, or anything
        // above plain Member.
        if (user is null || !organizationActive || !user.IsServiceAccount || !membership.UserExists
            || membership.Role is not OrganizationRole.Member)
        {
            Logger.LogInformation("API key {KeyId} rejected: service account inactive", key.KeyId);
            return Failure(ApiKeyAuthentication.Inactive);
        }

        OrganizationRole role = OrganizationRole.Member;
        string scope = string.Join(' ', key.Scopes);
        ClaimsIdentity identity = new(
        [
            new Claim("sub", key.ServiceUserId.ToString()),
            new Claim("email", user.Email),
            new Claim(AuthClaims.Organization, key.OrganizationId.ToString()),
            new Claim(AuthClaims.OrgRole, role.ToString()),
            new Claim(AuthClaims.EmailVerified, user.EmailVerifiedAt is not null ? "true" : "false"),
            // Never platform authority, even when the creator is a platform admin (the creator is not the principal).
            new Claim(AuthClaims.PlatformAdmin, "false"),
            new Claim(AuthClaims.IsAdminAlias, "false"),
            new Claim(AuthClaims.Scope, scope),
            new Claim(AuthClaims.ApiKeyId, key.Id.ToString())
        ], ApiKeyAuthentication.Scheme);

        Context.Items[HttpApiKeyContext.ItemKey] = new ApiKeyRequestInfo(
            key.Id, key.OrganizationId, key.ProjectIds?.ToArray(), key.Scopes.ToArray());
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private AuthenticateResult Failure(string code)
    {
        Context.Items[ApiKeyAuthentication.FailureItem] = code;
        return AuthenticateResult.Fail(code);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        string code = Context.Items.TryGetValue(ApiKeyAuthentication.FailureItem, out object? value) && value is string s
            ? s
            : ApiKeyAuthentication.InvalidKey;
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers["X-Request-Id"] = ReelBolt.Inference.Api.Services.PublicApi.PublicApiRequestContext.RequestId(Context);
        Response.Headers.WWWAuthenticate = "Bearer";
        Response.ContentType = "application/json";
        await Response.WriteAsJsonAsync(new { error = new { code, message = ApiKeyAuthentication.MessageFor(code) } });
    }
}
