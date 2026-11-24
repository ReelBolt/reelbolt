using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Auth;

/// <summary>
/// The database re-check every JwtBearer service (Inference API, workflow engine) runs in its
/// <c>OnTokenValidated</c> hook. The token's <c>org</c>/<c>orgRole</c>/<c>platformAdmin</c> claims are only a
/// hint: this re-reads <c>application_users</c> and <c>organization_memberships</c> (cached for <see cref="Ttl"/>),
/// so a removed user, a removed member, or a token issued before <c>tokens_invalidated_at</c> is rejected (401),
/// and a <c>platformAdmin</c> claim is honoured only while <c>application_users.is_admin</c> still agrees (a demoted
/// admin gets 403 on admin routes within the cache window). Validated values REPLACE the token's claims.
/// Works over any <see cref="DbContext"/> that maps <see cref="ApplicationUser"/> and <see cref="OrganizationMembership"/>.
/// </summary>
public class TokenMembershipValidator(IMemoryCache cache, TimeProvider? time = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    protected TimeProvider Time { get; } = time ?? TimeProvider.System;

    /// <summary>What the database says about one (user, org) pair.</summary>
    public sealed record Membership(
        bool UserExists,
        bool IsPlatformAdmin,
        OrganizationRole? Role,
        DateTime? TokensInvalidatedAt = null,
        bool IsServiceAccount = false);

    /// <summary>
    /// Pure claim rewrite, separated so it is testable without a JwtBearer pipeline. Returns false (with a reason)
    /// when the token must be rejected.
    /// </summary>
    public static bool Apply(
        ClaimsPrincipal principal, Membership membership, Guid? orgId, out string? failure, bool honourEngineActor = true)
    {
        failure = null;
        if (!membership.UserExists)
        {
            failure = "User no longer exists.";
            return false;
        }

        // A service account (the hidden user behind one API key) is never a person and never signs in: a JwtBearer
        // token for it is refused. The single exception is the workflow engine's on-behalf-of token
        // (act == workflow-engine), which the engine mints for the initiator of a key-started run. That token is HS256
        // under the same shared signing key as the Go API's session tokens, so anyone able to forge one could already
        // forge a token for any person: this exception widens nothing. The key is only ever exercised by the engine.
        // With an engine key configured the exception applies only to tokens the engine scheme validated
        // (honourEngineActor is false for the session scheme).
        if (membership.IsServiceAccount && !(honourEngineActor && IsEngineOnBehalfOf(principal)))
        {
            failure = "Service accounts cannot use a session token.";
            return false;
        }

        if (IsIssuedBeforeInvalidation(principal, membership.TokensInvalidatedAt))
        {
            failure = "Token was issued before the account's sessions were invalidated.";
            return false;
        }

        if (orgId is not null && membership.Role is null)
        {
            failure = "User is not a member of the token's organization.";
            return false;
        }

        bool claimedAdmin = AuthClaims.ReadPlatformAdmin(principal);
        bool platformAdmin = claimedAdmin && membership.IsPlatformAdmin && !membership.IsServiceAccount;

        ClaimsIdentity identity = principal.Identities.First();
        StripAuthorityClaims(identity);

        if (membership.Role is { } role)
            identity.AddClaim(new Claim(AuthClaims.OrgRole, role.ToString()));
        string flag = platformAdmin ? "true" : "false";
        identity.AddClaim(new Claim(AuthClaims.PlatformAdmin, flag));
        identity.AddClaim(new Claim(AuthClaims.IsAdminAlias, flag));
        return true;
    }

    /// <summary>
    /// For a principal with no database user behind it (e.g. a runner's <c>device:</c> subject): it can never be a
    /// platform admin or hold an org role, so any such claims it carries are removed.
    /// </summary>
    public static void StripAuthorityClaims(ClaimsIdentity identity)
    {
        foreach (string type in new[] { AuthClaims.OrgRole, AuthClaims.PlatformAdmin, AuthClaims.IsAdminAlias })
        {
            foreach (Claim stale in identity.FindAll(type).ToList())
                identity.RemoveClaim(stale);
        }
    }

    public static bool IsEngineOnBehalfOf(ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirstValue(OnBehalfOfTokenIssuer.ActorClaim), OnBehalfOfTokenIssuer.ActorValue, StringComparison.Ordinal);

    /// <summary>
    /// True when the token's <c>iat</c> (whole seconds) predates <c>application_users.tokens_invalidated_at</c>
    /// (password reset/change, deletion request). The cutoff is truncated to the second so a token minted
    /// right after the reset in the same second still works. A token without <c>iat</c> fails closed once
    /// an invalidation exists. Mirrors the Go API's <c>middleware.Auth</c> check.
    /// </summary>
    public static bool IsIssuedBeforeInvalidation(ClaimsPrincipal principal, DateTime? invalidatedAt)
    {
        if (invalidatedAt is not { } cutoff)
            return false;
        if (!long.TryParse(principal.FindFirstValue("iat"), out long iat))
            return true;
        long cutoffSeconds = new DateTimeOffset(DateTime.SpecifyKind(cutoff, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return iat < cutoffSeconds;
    }

    public async Task<Membership> LookupAsync(DbContext db, Guid userId, Guid? orgId, CancellationToken ct)
    {
        string key = CacheKey(userId, orgId);
        if (cache.TryGetValue(key, out Membership? cached) && cached is not null)
            return cached;

        var userRow = await db.Set<ApplicationUser>().AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsAdmin, u.TokensInvalidatedAt, u.IsServiceAccount })
            .FirstOrDefaultAsync(ct);
        bool? isAdmin = userRow?.IsAdmin;

        OrganizationRole? role = null;
        if (isAdmin is not null && orgId is { } org)
        {
            // Nullable projection: a missing row must read as "no membership", not as the enum default (Owner).
            role = await db.Set<OrganizationMembership>().AsNoTracking()
                .Where(m => m.OrganizationId == org && m.UserId == userId)
                .Select(m => (OrganizationRole?)m.Role)
                .FirstOrDefaultAsync(ct);
        }

        Membership result = new(isAdmin is not null, isAdmin ?? false, role, userRow?.TokensInvalidatedAt, userRow?.IsServiceAccount ?? false);
        cache.Set(key, result, Ttl);
        return result;
    }

    /// <summary>
    /// The whole check for one principal, minus anything service-specific (the Inference API adds its API-key
    /// check for on-behalf-of tokens). Returns the refusal reason, or null when the principal was accepted and
    /// its claims rewritten to database truth.
    /// </summary>
    public async Task<string?> ValidatePrincipalAsync(ClaimsPrincipal? principal, DbContext db, CancellationToken ct)
    {
        string? sub = principal?.FindFirstValue("sub") ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal is null || !Guid.TryParse(sub, out Guid userId))
            return "Token has no valid subject.";

        Guid? orgId = AuthClaims.ReadOrganizationId(principal);
        if (principal.FindFirstValue(AuthClaims.Organization) is { Length: > 0 } && orgId is null)
            return "Token carries a malformed organization claim.";

        Membership membership = await LookupAsync(db, userId, orgId, ct);
        return Apply(principal, membership, orgId, out string? failure) ? null : failure;
    }

    /// <summary>Drops the cached answer for one pair (membership changes call this so they apply immediately).</summary>
    public void Invalidate(Guid userId, Guid? orgId) => cache.Remove(CacheKey(userId, orgId));

    private static string CacheKey(Guid userId, Guid? orgId) => $"membership:{userId:N}:{orgId?.ToString("N") ?? "-"}";
}
