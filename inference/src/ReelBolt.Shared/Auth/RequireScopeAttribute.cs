namespace ReelBolt.Shared.Auth;

/// <summary>
/// Endpoint metadata: a scoped principal (an API key) is admitted on this endpoint when it holds ANY of
/// <see cref="AnyOf"/>. Without this attribute a scoped principal reaches only the MCP allowlist.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireScopeAttribute(params string[] anyOf) : Attribute
{
    public IReadOnlyList<string> AnyOf { get; } = anyOf;
}
