namespace ReelBolt.Shared.Auth;

/// <summary>
/// Endpoint metadata for a public API endpoint that any valid API key may call, whatever its scopes
/// (today only <c>GET /me</c>). A public endpoint with neither this nor <see cref="RequireScopeAttribute"/>
/// is refused for every key (fail closed).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AnyApiKeyScopeAttribute : Attribute;
