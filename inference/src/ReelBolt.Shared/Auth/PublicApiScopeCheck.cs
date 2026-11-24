using System.Reflection;
using System.Security.Claims;

namespace ReelBolt.Shared.Auth;

public enum PublicApiScopeVerdict
{
    Allowed,
    Insufficient,
    Undeclared
}

/// <summary>The verdict plus, for <see cref="PublicApiScopeVerdict.Insufficient"/>, the scopes of which one was needed.</summary>
public sealed record PublicApiScopeResult(PublicApiScopeVerdict Verdict, IReadOnlyList<string> RequiredScopes)
{
    public bool IsAllowed => Verdict == PublicApiScopeVerdict.Allowed;
}

/// <summary>
/// The one scope decision for a public endpoint, shared by the scope-guard middleware and the public guard
/// filter so they cannot drift. Fail closed: an endpoint declaring neither attribute is refused.
/// </summary>
public static class PublicApiScopeCheck
{
    public const string UndeclaredCode = "scope_not_declared";

    public static PublicApiScopeResult Evaluate(ClaimsPrincipal user, IEnumerable<object>? metadata)
        => Evaluate(user, metadata, method: null);

    /// <summary>
    /// Evaluates the endpoint's scope declaration. When the action <paramref name="method"/> is known, only
    /// its OWN attributes count: a class-level <see cref="RequireScopeAttribute"/> or
    /// <see cref="AnyApiKeyScopeAttribute"/> never widens (or narrows) an action, method metadata wins.
    /// Public controllers are forbidden from declaring either at class level; a sweep test enforces that.
    /// </summary>
    public static PublicApiScopeResult Evaluate(ClaimsPrincipal user, IEnumerable<object>? metadata, MethodInfo? method)
    {
        object[] items = method is { } action
            ? action.GetCustomAttributes(inherit: true)
            : metadata?.ToArray() ?? [];
        if (items.OfType<AnyApiKeyScopeAttribute>().Any())
            return new PublicApiScopeResult(PublicApiScopeVerdict.Allowed, []);

        string[] required = items.OfType<RequireScopeAttribute>()
            .SelectMany(a => a.AnyOf).Distinct(StringComparer.Ordinal).ToArray();
        if (required.Length == 0)
            return new PublicApiScopeResult(PublicApiScopeVerdict.Undeclared, []);

        HashSet<string> held = McpScopeGuard.ReadScopes(user);
        return required.Any(held.Contains)
            ? new PublicApiScopeResult(PublicApiScopeVerdict.Allowed, required)
            : new PublicApiScopeResult(PublicApiScopeVerdict.Insufficient, required);
    }
}
