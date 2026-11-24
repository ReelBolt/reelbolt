using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>
/// S-6: the audit row for an API key the scope-guard MIDDLEWARE refused. Such a request never reaches
/// <see cref="PublicApiGuardFilter"/>, so without this an <c>insufficient_scope</c>, a
/// <c>scope_not_declared</c> or an <c>api_key_scope</c> refusal would be invisible in the key's trail.
/// The route recorded is the endpoint's TEMPLATE, else the literal <c>(unmatched)</c> - never the raw path.
/// </summary>
public sealed class ApiKeyScopeRefusalAuditor(IApiKeyAuditWriter audit, TimeProvider? time = null) : IScopeRefusalAuditor
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public void RecordScopeRefusal(HttpContext context, string code)
    {
        _ = code; // the audit row carries the status; the refusal code is already in the response body
        if (!Guid.TryParse(context.User.FindFirst(AuthClaims.ApiKeyId)?.Value, out Guid keyId))
            return;

        Guid organizationId = AuthClaims.ReadOrganizationId(context.User)
            ?? context.RequestServices?.GetService<IApiKeyContext>()?.OrganizationId
            ?? Guid.Empty;

        audit.TryWrite(new ApiKeyAuditEntry(
            keyId,
            organizationId,
            _time.GetUtcNow().UtcDateTime,
            context.Request.Method,
            ScopeRefusalAudit.RouteTemplate(context),
            StatusCodes.Status403Forbidden,
            0,
            PublicApiRequestContext.RequestId(context)));
    }
}
