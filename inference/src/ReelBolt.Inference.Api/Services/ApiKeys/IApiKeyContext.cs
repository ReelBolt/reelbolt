namespace ReelBolt.Inference.Api.Services.ApiKeys;

/// <summary>What the ApiKey handler established about the current request's key.</summary>
public sealed record ApiKeyRequestInfo(
    Guid KeyId,
    Guid OrganizationId,
    IReadOnlyList<Guid>? AllowedProjectIds,
    IReadOnlyCollection<string> Scopes);

/// <summary>The API key behind the current request, or null members when the caller is not an API key.</summary>
public interface IApiKeyContext
{
    /// <summary>True when the request was authenticated with an API key.</summary>
    bool IsApiKey { get; }

    Guid? KeyId { get; }

    Guid? OrganizationId { get; }

    /// <summary>The projects the key is limited to; null = no project restriction.</summary>
    IReadOnlyList<Guid>? AllowedProjectIds { get; }

    IReadOnlyCollection<string> Scopes { get; }
}

public sealed class HttpApiKeyContext(IHttpContextAccessor accessor) : IApiKeyContext
{
    internal const string ItemKey = "ReelBolt.ApiKey.Request";

    private ApiKeyRequestInfo? Info =>
        accessor.HttpContext?.Items.TryGetValue(ItemKey, out object? value) == true ? value as ApiKeyRequestInfo : null;

    public bool IsApiKey => Info is not null;
    public Guid? KeyId => Info?.KeyId;
    public Guid? OrganizationId => Info?.OrganizationId;
    public IReadOnlyList<Guid>? AllowedProjectIds => Info?.AllowedProjectIds;
    public IReadOnlyCollection<string> Scopes => Info?.Scopes ?? [];
}
