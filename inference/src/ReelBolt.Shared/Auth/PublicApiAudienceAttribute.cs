namespace ReelBolt.Shared.Auth;

/// <summary>
/// Endpoint metadata of the public API (<c>/api/public/v1/**</c>). The API-key scope guard admits a scoped
/// principal here and lets the public controller's own guard decide: the public surface is key-only, so a
/// session JWT or an MCP token is answered <c>401 api_key_required</c> there, not by the dashboard guard.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PublicApiAudienceAttribute : Attribute
{
}
