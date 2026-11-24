namespace ReelBolt.Inference.Api.Controllers.Dto;

// Social publishing (docs/social-publishing.md). JSON is camelCase; `provider` is always the URL slug
// ("youtube", "linkedin"). No response ever carries a token.

/// <summary>One platform of <c>GET /api/v1/social/providers</c>. Only platforms this deployment has credentials for are listed.</summary>
public sealed record SocialProviderResponse(string Provider, string DisplayName);

/// <summary>One connected account of the active organization.</summary>
public sealed record SocialAccountResponse(
    Guid Id,
    string Provider,
    string? DisplayName,
    string? AvatarUrl,
    string Status,
    string? LastError,
    DateTime CreatedAt);

/// <summary>Body of <c>POST {provider}/connect</c>: where to come back to (a <c>/app/…</c> path; anything else is ignored).</summary>
public sealed record SocialConnectRequest(string? ReturnTo = null);

public sealed record SocialConnectResponse(string AuthorizeUrl);
