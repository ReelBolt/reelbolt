using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Auth;

/// <summary>
/// Runs as the JwtBearer <c>OnTokenValidated</c> hook. The token's <c>org</c>/<c>orgRole</c>/<c>platformAdmin</c>
/// claims are only a hint: this re-reads the database membership (cached for <see cref="Ttl"/>), so a member
/// removed or demoted after the token was issued is rejected (401) or downgraded within seconds, and a
/// <c>platformAdmin</c> claim is honoured only while <c>application_users.is_admin</c> still agrees.
/// Validated values REPLACE the token's claims on the principal, so every later reader
/// (<c>ICurrentUser</c>, <c>ITenantContext</c>) sees database truth.
/// The rules themselves (lookup, cache, claim rewrite) live in <see cref="TokenMembershipValidator"/> so the
/// workflow engine applies exactly the same ones; this subclass adds the API-key check for on-behalf-of tokens.
/// </summary>
public sealed class MembershipValidator(IMemoryCache cache, ILogger<MembershipValidator> logger, TimeProvider? time = null)
    : TokenMembershipValidator(cache, time)
{
    public Task ValidateAsync(TokenValidatedContext context) => ValidateAsync(context, EngineActorMode.Legacy);

    /// <summary>
    /// <paramref name="mode"/> says which scheme validated the token and whether an engine key is configured
    /// (<see cref="EngineActorMode"/>). With an engine key, <c>act=workflow-engine</c> is honoured only on a
    /// token the engine scheme validated: on a session-key token it can only be a forgery or a leftover.
    /// </summary>
    public async Task ValidateAsync(TokenValidatedContext context, EngineActorMode mode)
    {
        ClaimsPrincipal? principal = context.Principal;
        bool legacyEngineToken = false;
        if (principal is not null && mode != EngineActorMode.Legacy)
        {
            bool actor = EngineTokenAuthentication.CarriesEngineActor(principal);
            legacyEngineToken = mode == EngineActorMode.SessionSchemeAcceptingLegacy && actor;
            if (mode == EngineActorMode.SessionSchemeWithEngineKey && actor)
            {
                context.Fail("A session token cannot carry the workflow engine marker.");
                return;
            }

            if (mode == EngineActorMode.EngineScheme)
            {
                if (!actor)
                {
                    context.Fail("An engine token must carry the workflow engine marker.");
                    return;
                }

                // An engine call never acts with platform authority, whatever the token claims.
                foreach (ClaimsIdentity identity in principal.Identities)
                    foreach (Claim claim in identity.FindAll(c => c.Type is AuthClaims.PlatformAdmin or AuthClaims.IsAdminAlias).ToList())
                        identity.RemoveClaim(claim);
            }
        }

        bool honourEngineActor = mode != EngineActorMode.SessionSchemeWithEngineKey;
        string? sub = principal?.FindFirstValue("sub") ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal is null || !Guid.TryParse(sub, out Guid userId))
        {
            context.Fail("Token has no valid subject.");
            return;
        }

        Guid? orgId = AuthClaims.ReadOrganizationId(principal);
        if (principal.FindFirstValue(AuthClaims.Organization) is { Length: > 0 } && orgId is null)
        {
            context.Fail("Token carries a malformed organization claim.");
            return;
        }

        InferenceApiDbContext db = context.HttpContext.RequestServices.GetRequiredService<InferenceApiDbContext>();
        Membership membership = await LookupAsync(db, userId, orgId, context.HttpContext.RequestAborted);

        // S-M3c: the service-account exception exists only for the engine's on-behalf-of token, and only
        // while the key that produced it is still active. Revoking the key therefore stops its work here.
        if (membership.IsServiceAccount && honourEngineActor && IsEngineOnBehalfOf(principal))
        {
            if (await AuthorizeOnBehalfOfKeyAsync(
                    context.HttpContext, principal, userId, orgId, db, context.HttpContext.RequestAborted) is { } keyFailure)
            {
                logger.LogWarning("Rejected on-behalf-of token for service user {UserId}: {Reason}", userId, keyFailure);
                context.Fail(keyFailure);
                return;
            }
        }

        if (!Apply(principal, membership, orgId, out string? failure, honourEngineActor))
        {
            logger.LogWarning("Rejected token for user {UserId} in org {OrgId}: {Reason}", userId, orgId, failure);
            context.Fail(failure!);
            return;
        }

        if (legacyEngineToken)
        {
            // Accepted only for the cut-over, and only where an engine token is: the guard confines it.
            EngineTokenAuthentication.MarkEnginePrincipal(context.HttpContext);
            EngineTokenAuthentication.LegacyAccepted.Add(1);
            logger.LogWarning(
                "Accepted a session-key on-behalf-of token for user {UserId} (EngineJwt:AcceptLegacyOnBehalfOf is on). Turn the setting off once the engine host runs with the engine key.",
                userId);
        }
    }

    /// <summary>
    /// S-M3c/S-M5: an on-behalf-of token for a service account must name the API key that started the
    /// run, and that key must still be active (exists, not revoked, not expired, same organization and
    /// service user). When it is, the key is put on the request's <see cref="IApiKeyContext"/> so
    /// <c>ProjectAccessService</c> keeps enforcing the key's project list on engine callbacks.
    /// Deliberately no <c>scope</c> claim is added: engine callbacks hit dashboard routes, and the
    /// scope guard would refuse a scoped principal there.
    /// Returns the refusal reason, or null when the key is acceptable.
    /// </summary>
    public async Task<string?> AuthorizeOnBehalfOfKeyAsync(
        HttpContext http, ClaimsPrincipal principal, Guid userId, Guid? orgId, InferenceApiDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(AuthClaims.ApiKeyId), out Guid keyId))
            return "An on-behalf-of token for a service account must carry its API key.";

        ApiKey? key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId, ct);
        DateTime now = Time.GetUtcNow().UtcDateTime;
        bool active = key is not null
            && key.RevokedAt is null
            && (key.ExpiresAt is not { } expires || DateTime.SpecifyKind(expires, DateTimeKind.Utc) > now)
            && key.ServiceUserId == userId
            && orgId is { } org && key.OrganizationId == org;
        if (!active)
            return "The API key behind this on-behalf-of token is no longer active.";

        http.Items[HttpApiKeyContext.ItemKey] = new ApiKeyRequestInfo(
            key!.Id, key.OrganizationId, key.ProjectIds?.ToArray(), key.Scopes.ToArray());
        return null;
    }
}
