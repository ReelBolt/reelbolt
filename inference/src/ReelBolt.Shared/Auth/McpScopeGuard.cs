using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ReelBolt.Shared.Auth;

/// <summary>One route an MCP-scoped token may reach: exact method and path, and the scopes of which at least one is required.</summary>
public sealed record McpAllowedRoute(string Method, string Path, params string[] AnyOfScopes);

/// <summary>Reads the <c>scope</c> claim set of a principal.</summary>
public static class McpScopeGuard
{
    public const string ErrorCode = "mcp_token_scope";
    public const string ErrorMessage = "This token was issued for MCP clients and cannot be used on this endpoint.";

    /// <summary>Refusal code for an API key on an endpoint that declares no <see cref="RequireScopeAttribute"/>.</summary>
    public const string ApiKeyErrorCode = "api_key_scope";
    public const string ApiKeyErrorMessage = "API keys can only be used on the public API endpoints.";

    /// <summary>Refusal code for a scoped principal on a scoped endpoint without the scope.</summary>
    public const string InsufficientScopeCode = "insufficient_scope";

    /// <summary>True when the principal was built from an API key.</summary>
    public static bool IsApiKey(ClaimsPrincipal? user) => user?.HasClaim(c => c.Type == AuthClaims.ApiKeyId) == true;

    /// <summary>True when the principal carries any <c>scope</c> claim, even an empty or unknown one.</summary>
    public static bool IsScoped(ClaimsPrincipal? user) => user?.HasClaim(c => c.Type == AuthClaims.Scope) == true;

    /// <summary>All scope values: every <c>scope</c> claim, split on spaces.</summary>
    public static HashSet<string> ReadScopes(ClaimsPrincipal? user)
    {
        HashSet<string> scopes = new(StringComparer.Ordinal);
        if (user is null) return scopes;
        foreach (Claim claim in user.FindAll(AuthClaims.Scope))
            foreach (string part in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                scopes.Add(part);
        return scopes;
    }

    internal static string NormalizePath(string? path)
    {
        string p = path ?? string.Empty;
        if (p.Length > 1 && p.EndsWith('/')) p = p[..^1];
        return p;
    }

    public static bool IsAllowed(ClaimsPrincipal user, string method, string? path, IReadOnlyList<McpAllowedRoute> allowed)
    {
        HashSet<string> scopes = ReadScopes(user);
        string normalized = NormalizePath(path);
        foreach (McpAllowedRoute route in allowed)
        {
            if (!string.Equals(route.Method, method, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(NormalizePath(route.Path), normalized, StringComparison.OrdinalIgnoreCase)) continue;
            if (route.AnyOfScopes.Any(scopes.Contains)) return true;
        }
        return false;
    }

    /// <summary>Deny-by-default for MCP OAuth tokens. Register right after <c>UseAuthentication</c>.</summary>
    public static IApplicationBuilder UseMcpScopeGuard(this IApplicationBuilder app, params McpAllowedRoute[] allowed)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<McpScopeGuardMiddleware>((IReadOnlyList<McpAllowedRoute>)allowed);
    }
}

public sealed class McpScopeGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<McpAllowedRoute> _allowed;

    public McpScopeGuardMiddleware(RequestDelegate next, IReadOnlyList<McpAllowedRoute> allowed)
    {
        _next = next;
        _allowed = allowed;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ClaimsPrincipal user = context.User;
        if (user.Identity?.IsAuthenticated == true && McpScopeGuard.IsScoped(user))
        {
            // The public API is key-only and enforces that itself (401 api_key_required for anything that is
            // not an API-key principal); its own guard also runs the rate limit and the audit. The dashboard
            // guard must not answer `api_key_scope` for these endpoints (which would hide that rule).
            if (context.GetEndpoint()?.Metadata.GetMetadata<PublicApiAudienceAttribute>() is not null)
            {
                // An API key must hold a scope the endpoint declares (fail closed: an undeclared public
                // endpoint is refused). Any other scoped principal (an MCP token) is passed on so the
                // filter answers 401 api_key_required - the only refusal this branch skips.
                if (McpScopeGuard.IsApiKey(user))
                {
                    PublicApiScopeResult verdict = PublicApiScopeCheck.Evaluate(user, context.GetEndpoint()?.Metadata,
                        context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()?.MethodInfo);
                    if (verdict.Verdict == PublicApiScopeVerdict.Insufficient)
                    {
                        await Refuse(context, McpScopeGuard.InsufficientScopeCode,
                            "This key lacks the scope this endpoint requires.", verdict.RequiredScopes.ToArray());
                        return;
                    }

                    if (verdict.Verdict == PublicApiScopeVerdict.Undeclared)
                    {
                        await Refuse(context, PublicApiScopeCheck.UndeclaredCode,
                            "This endpoint is not available to API keys.", null);
                        return;
                    }
                }

                await _next(context);
                return;
            }

            // (a) the MCP allowlist, (b) an endpoint that declares RequireScope and a scope the principal holds.
            if (!McpScopeGuard.IsAllowed(user, context.Request.Method, context.Request.Path.Value, _allowed))
            {
                string[] required = context.GetEndpoint()?.Metadata.GetOrderedMetadata<RequireScopeAttribute>()
                    .SelectMany(a => a.AnyOf).Distinct(StringComparer.Ordinal).ToArray() ?? [];
                if (required.Length > 0)
                {
                    HashSet<string> held = McpScopeGuard.ReadScopes(user);
                    if (!required.Any(held.Contains))
                    {
                        await Refuse(context, McpScopeGuard.InsufficientScopeCode,
                            "This key lacks the scope this endpoint requires.", required);
                        return;
                    }
                }
                else if (McpScopeGuard.IsApiKey(user))
                {
                    await Refuse(context, McpScopeGuard.ApiKeyErrorCode, McpScopeGuard.ApiKeyErrorMessage, null);
                    return;
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new { error = McpScopeGuard.ErrorCode, message = McpScopeGuard.ErrorMessage });
                    return;
                }
            }
        }
        await _next(context);
    }

    private static async Task Refuse(HttpContext context, string code, string message, string[]? requiredScopes)
    {
        // A key refused here never reaches PublicApiGuardFilter, so its audit row is written from here.
        if (McpScopeGuard.IsApiKey(context.User))
            context.RequestServices?.GetService<IScopeRefusalAuditor>()?.RecordScopeRefusal(context, code);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        object error = requiredScopes is null
            ? new { code, message }
            : new { code, message, requiredScopes };
        await context.Response.WriteAsJsonAsync(new { error });
    }
}
