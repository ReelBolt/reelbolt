using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>The request id the public surface reports (<c>X-Request-Id</c>) and audits.</summary>
public static class PublicApiRequestContext
{
    public const string HeaderName = "X-Request-Id";

    /// <summary>Reuses the current activity's trace id when there is one, else the framework's identifier.</summary>
    public static string RequestId(HttpContext context) =>
        System.Diagnostics.Activity.Current?.TraceId.ToString() is { Length: > 0 } trace ? trace : context.TraceIdentifier;

    /// <summary>The endpoint's route TEMPLATE (never the raw path or query).</summary>
    public static string RouteTemplate(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
        ?? context.Request.Path.Value?.TrimStart('/')
        ?? string.Empty;
}

/// <summary>
/// Every <c>/api/public/v1/**</c> action's shared gate, in order:
/// <list type="number">
/// <item>flag off -> 404 with an empty body;</item>
/// <item>not an API-key principal (a session JWT, an MCP token) -> 401 <c>api_key_required</c>;</item>
/// <item>the per-key rate limit -> 429 with <c>Retry-After</c> and <c>error.resetsAt</c>;</item>
/// <item>otherwise the action, audited afterwards with the route template and status.</item>
/// </list>
/// It sits outside the idempotency filter (<see cref="IdempotentAttribute"/> order 100), so a replay or a
/// 429 is answered after the same checks as the first request and is still audited.
/// </summary>
public sealed class PublicApiGuardFilter(
    IOptionsMonitor<PublicApiOptions> options,
    IApiKeyContext keyContext,
    ApiKeyRateLimiter rateLimiter,
    IApiKeyAuditWriter audit,
    TimeProvider? time = null,
    IOrganizationEntitlements? entitlements = null) : IAsyncResourceFilter, IOrderedFilter
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Runs before the idempotency filter and any action filter.</summary>
    public int Order => 0;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        HttpContext http = context.HttpContext;
        http.Response.Headers[PublicApiRequestContext.HeaderName] = PublicApiRequestContext.RequestId(http);

        if (!options.CurrentValue.Enabled)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string? keyClaim = http.User.FindFirst(AuthClaims.ApiKeyId)?.Value;
        if (http.User.Identity?.IsAuthenticated != true || !Guid.TryParse(keyClaim, out Guid apiKeyId))
        {
            await PublicApiEnvelope.WriteAsync(http, StatusCodes.Status401Unauthorized,
                PublicApiErrorCodes.ApiKeyRequired, PublicApiErrorCodes.ApiKeyRequiredMessage);
            return;
        }

        // Defence in depth: the scope-guard middleware refuses first; the same shared check runs here too.
        Guid organizationId = keyContext.OrganizationId
            ?? AuthClaims.ReadOrganizationId(http.User)
            ?? Guid.Empty;
        string route = PublicApiRequestContext.RouteTemplate(http);
        long started = Stopwatch.GetTimestamp();

        void Audit(int status) => audit.TryWrite(new ApiKeyAuditEntry(
            apiKeyId, organizationId, _time.GetUtcNow().UtcDateTime, http.Request.Method, route, status,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, PublicApiRequestContext.RequestId(http)));

        PublicApiScopeResult scope = PublicApiScopeCheck.Evaluate(http.User, context.ActionDescriptor.EndpointMetadata,
            (context.ActionDescriptor as Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor)?.MethodInfo);

        bool isWrite = http.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE";
        ApiKeyRateLimitDecision decision = await rateLimiter.CheckAsync(apiKeyId, isWrite, _time.GetUtcNow(), http.RequestAborted);
        http.Response.Headers["RateLimit-Limit"] = decision.RequestsLimit.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["RateLimit-Reset"] = decision.ResetSeconds.ToString(CultureInfo.InvariantCulture);

        if (!scope.IsAllowed)
        {
            bool undeclared = scope.Verdict == PublicApiScopeVerdict.Undeclared;
            await PublicApiEnvelope.WriteAsync(http, StatusCodes.Status403Forbidden,
                undeclared ? PublicApiScopeCheck.UndeclaredCode : McpScopeGuard.InsufficientScopeCode,
                undeclared ? "This endpoint is not available to API keys." : "This key lacks the scope this endpoint requires.",
                undeclared ? null : new { requiredScopes = scope.RequiredScopes });
            Audit(StatusCodes.Status403Forbidden);
            return;
        }

        if (!decision.Allowed)
        {
            await PublicApiEnvelope.WriteRateLimitedAsync(http, decision.ResetsAt,
                $"The API key exceeded its {decision.ViolatedLimit} per minute limit.", http.RequestAborted);
            Audit(StatusCodes.Status429TooManyRequests);
            return;
        }

        // Runs AFTER the per-key rate limiter, so a refused trial write is counted and throttled like any call.
        // S-L4: a key on a plan with no API access (Trial) is read-only: reads pass, every write is refused with
        // API_PLAN_READ_ONLY before anything is held, uploaded or recorded. Billing off yields the unlimited set, so self-hosted installations stay open.
        if (entitlements is not null && organizationId != Guid.Empty)
        {
            OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, http.RequestAborted);
            if (!PublicApiReadRule.IsRead(http.Request.Method, route) && PlanGate.CheckApiWrite(set) is { } violation)
            {
                await PublicApiEnvelope.WriteAsync(http, violation.StatusCode, violation.Code, violation.Message,
                    new { feature = violation.Feature });
                Audit(violation.StatusCode);
                return;
            }
        }

        int auditStatus = StatusCodes.Status500InternalServerError;
        try
        {
            await next();
            auditStatus = http.Response.StatusCode;
        }
        finally
        {
            // Info (T-022): a thrown action is still audited, as the 500 it becomes, never dropped from the trail.
            Audit(auditStatus);
        }
    }
}
