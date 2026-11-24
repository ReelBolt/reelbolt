using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Services.Auth;

/// <summary>One route an engine token may reach: <c>{Method} {Prefix}{guid}{Suffix}</c>.</summary>
public sealed record EngineAllowedRoute(string Method, string Prefix, string Suffix);

/// <summary>
/// The second bearer scheme of the Inference API, active only when <c>EngineJwt:SigningKey</c> is set
/// (<see cref="EngineJwtSettings"/>). It validates the WorkflowEngine's on-behalf-of tokens (engine key,
/// engine issuer and audience). A principal from this scheme reaches ONLY the routes in
/// <see cref="Allowed"/>: everything else is refused with <c>403</c>, so a stolen engine key cannot be
/// used as a user session.
/// </summary>
public static class EngineTokenAuthentication
{
    public const string Scheme = "EngineBearer";
    public const string ErrorCode = "engine_token_route";
    public const string ErrorMessage = "This token was issued for the workflow engine and cannot be used on this endpoint.";

    internal const string PrincipalItem = "ReelBolt.EngineToken.Principal";

    /// <summary>Counts session-key on-behalf-of tokens accepted during a cut-over; it should reach zero and stay there.</summary>
    public const string LegacyCounterName = "reelbolt.auth.legacy_engine_on_behalf_of";

    public const string MeterName = "ReelBolt.Inference.Api.Auth";

    private static readonly System.Diagnostics.Metrics.Meter Meter = new(MeterName);

    internal static readonly System.Diagnostics.Metrics.Counter<long> LegacyAccepted =
        Meter.CreateCounter<long>(LegacyCounterName);

    private const string ProjectsPrefix = "/api/v1/projects/";

    /// <summary>Route suffix of the design-system ingest the engine calls (DesignSystemStepExecutor).</summary>
    public const string DesignSystemIngestSuffix = "/design-system/ingest";

    /// <summary>Route suffix of the project file search the engine calls (ProjectFileAgentTools).</summary>
    public const string FileSearchSuffix = "/files/search";

    /// <summary>
    /// Every Inference API route the engine calls through its named <c>InferenceApi</c> client. Deny by
    /// default. A drift test ties this list to the engine's call sites (EngineTokenRouteDriftTests).
    /// </summary>
    public static readonly EngineAllowedRoute[] Allowed =
    [
        new("POST", ProjectsPrefix, DesignSystemIngestSuffix),
        new("POST", ProjectsPrefix, FileSearchSuffix)
    ];

    /// <summary>
    /// True when the request's bearer token names the engine issuer. The token is NOT trusted here: this
    /// only picks which scheme validates it, and that scheme then checks the signature, issuer, audience
    /// and lifetime. A session-key token that names the engine issuer therefore fails the engine key check.
    /// </summary>
    public static bool IsEngineTokenRequest(HttpRequest request, string engineIssuer)
    {
        string header = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)) return false;
        string token = header[bearer.Length..].Trim();
        try
        {
            return string.Equals(new JsonWebToken(token).Issuer, engineIssuer, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Text.Json.JsonException
            or Microsoft.IdentityModel.Tokens.SecurityTokenException)
        {
            return false;
        }
    }

    /// <summary>Marks the request as authenticated by the engine scheme. Called from <c>OnTokenValidated</c>.</summary>
    public static void MarkEnginePrincipal(HttpContext context) => context.Items[PrincipalItem] = true;

    /// <summary>True when the request's principal came from the engine scheme.</summary>
    public static bool IsEnginePrincipal(HttpContext context) => context.Items.ContainsKey(PrincipalItem);

    public static bool IsAllowed(string method, string? path)
    {
        string p = path ?? string.Empty;
        if (p.Length > 1 && p.EndsWith('/')) p = p[..^1];
        foreach (EngineAllowedRoute route in Allowed)
        {
            if (!string.Equals(route.Method, method, StringComparison.OrdinalIgnoreCase)) continue;
            if (!p.StartsWith(route.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!p.EndsWith(route.Suffix, StringComparison.OrdinalIgnoreCase)) continue;
            int length = p.Length - route.Prefix.Length - route.Suffix.Length;
            // Exactly one segment between the prefix and the suffix, and it is a project id.
            if (length > 0 && Guid.TryParseExact(p.Substring(route.Prefix.Length, length), "D", out _)) return true;
        }
        return false;
    }

    /// <summary>Deny-by-default for engine tokens. Register right after <c>UseAuthentication</c>.</summary>
    public static IApplicationBuilder UseEngineTokenGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (IsEnginePrincipal(context) && !IsAllowed(context.Request.Method, context.Request.Path.Value))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { error = ErrorCode, message = ErrorMessage });
                return;
            }
            await next(context);
        });
    }

    /// <summary>True when the principal carries the engine's on-behalf-of marker (<c>act=workflow-engine</c>).</summary>
    public static bool CarriesEngineActor(ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirstValue(OnBehalfOfTokenIssuer.ActorClaim), OnBehalfOfTokenIssuer.ActorValue, StringComparison.Ordinal);
}

/// <summary>How <see cref="MembershipValidator"/> treats the <c>act=workflow-engine</c> marker on a token.</summary>
public enum EngineActorMode
{
    /// <summary>No engine key is configured: a session-key token carrying the marker is the engine's (as before).</summary>
    Legacy,

    /// <summary>An engine key is configured and the token came from the session scheme: the marker is refused.</summary>
    SessionSchemeWithEngineKey,

    /// <summary>
    /// Cut-over (<c>EngineJwt:AcceptLegacyOnBehalfOf</c>): an engine key is configured, the token came from the
    /// session scheme and a marker on it is still honoured as before the engine key existed, but the principal
    /// is confined to the engine's allowlisted routes and each use is logged and counted.
    /// </summary>
    SessionSchemeAcceptingLegacy,

    /// <summary>An engine key is configured and the token came from the engine scheme: the marker is required.</summary>
    EngineScheme
}
