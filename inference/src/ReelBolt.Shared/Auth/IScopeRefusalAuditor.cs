using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ReelBolt.Shared.Auth;

/// <summary>
/// Hook for an API-key principal the scope guard refuses before MVC: such a request never reaches
/// <c>PublicApiGuardFilter</c>, so without this the key's audit trail would silently miss it. The
/// Inference API implements it over its API-key audit writer; the default does nothing.
/// </summary>
public interface IScopeRefusalAuditor
{
    /// <summary>Records one refusal. Implementations must never throw into the request path.</summary>
    void RecordScopeRefusal(HttpContext context, string code);
}

/// <summary>The default when no audit backend is configured (the WorkflowEngine host, unit tests).</summary>
public sealed class NoOpScopeRefusalAuditor : IScopeRefusalAuditor
{
    /// <inheritdoc />
    public void RecordScopeRefusal(HttpContext context, string code)
    {
    }
}

/// <summary>Helpers shared by the guard and its auditor.</summary>
public static class ScopeRefusalAudit
{
    /// <summary>Route label when no endpoint matched. A refusal never records the raw request path.</summary>
    public const string UnmatchedRoute = "(unmatched)";

    /// <summary>The endpoint's route TEMPLATE, or <see cref="UnmatchedRoute"/> when there is none.</summary>
    public static string RouteTemplate(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? UnmatchedRoute;
}
