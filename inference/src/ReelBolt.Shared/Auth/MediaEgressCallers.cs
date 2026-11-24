using System.Security.Claims;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Auth;

/// <summary>
/// Which caller may give the permission to send media for one run (docs/design/app-inputs.md "Permission to send media
/// as an app field"). Each answer is a grantor name or null; null means "this caller may not", and the run gate then
/// refuses <c>inputs.allowMediaEgress = true</c> (fail closed).
/// </summary>
public static class MediaEgressCallers
{
    /// <summary>
    /// The signed-in user's own app session: an authenticated principal that is not an API key, carries no OAuth
    /// <c>scope</c> claim and is not the engine acting on someone's behalf (<c>act</c>). Anything else, a missing
    /// principal included, is null.
    /// </summary>
    public static string? AppSession(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true
        && !McpScopeGuard.IsApiKey(user)
        && !McpScopeGuard.IsScoped(user)
        && !user.HasClaim(c => c.Type == OnBehalfOfTokenIssuer.ActorClaim)
            ? MediaEgressPermission.ByApp
            : null;
}
