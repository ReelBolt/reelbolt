using System.Security.Claims;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Auth;

/// <summary>
/// Names of the JWT claims the Go API issues (camelCase) and the one place that reads them, so the
/// <c>platformAdmin</c>/<c>isAdmin</c> alias rule (D6: <c>isAdmin</c> is emitted for one release as an
/// alias of <c>platformAdmin</c>) is implemented once.
/// </summary>
public static class AuthClaims
{
    public const string Organization = "org";
    public const string OrgRole = "orgRole";
    public const string PlatformAdmin = "platformAdmin";
    public const string IsAdminAlias = "isAdmin";
    public const string EmailVerified = "emailVerified";
    public const string Scope = "scope";

    /// <summary>The API key id (<c>api_keys.id</c>) on a principal built from an API key; absent on every other principal.</summary>
    public const string ApiKeyId = "api_key_id";

    /// <summary><c>platformAdmin</c> when present, otherwise the legacy <c>isAdmin</c> alias.</summary>
    public static bool ReadPlatformAdmin(ClaimsPrincipal? user)
    {
        string? claim = user?.FindFirst(PlatformAdmin)?.Value ?? user?.FindFirst(IsAdminAlias)?.Value;
        return bool.TryParse(claim, out bool value) && value;
    }

    /// <summary>
    /// The <c>emailVerified</c> claim. Only an explicit <c>false</c> counts as unverified: tokens minted
    /// before the claim existed, and engine on-behalf-of tokens (which carry none), are treated as verified.
    /// </summary>
    public static bool ReadEmailVerified(ClaimsPrincipal? user)
    {
        string? claim = user?.FindFirst(EmailVerified)?.Value;
        return !(bool.TryParse(claim, out bool value) && !value);
    }

    public static Guid? ReadOrganizationId(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirst(Organization)?.Value, out Guid id) ? id : null;

    public static OrganizationRole? ReadOrgRole(ClaimsPrincipal? user) =>
        Enum.TryParse(user?.FindFirst(OrgRole)?.Value, ignoreCase: true, out OrganizationRole role)
        && Enum.IsDefined(role)
            ? role
            : null;
}
